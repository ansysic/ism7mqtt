﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using ism7mqtt.ISM7.Protocol;

namespace ism7mqtt
{
    public class Ism7Client
    {
        private readonly Func<MqttMessage, CancellationToken, Task> _messageHandler;
        private readonly IPAddress _ipAddress;
        private readonly ConcurrentDictionary<Type, XmlSerializer> _serializers = new ConcurrentDictionary<Type, XmlSerializer>();
        private readonly ConcurrentDictionary<string, SystemconfigResp.BusDevice> _devices = new ConcurrentDictionary<string, SystemconfigResp.BusDevice>();
        private readonly Ism7Config _config;
        private readonly Pipe _pipe;
        private readonly ResponseDispatcher _dispatcher = new ResponseDispatcher();
        private int _nextBundleId = 0;

        public bool EnableDebug { get; set; }

        public Ism7Client(Func<MqttMessage, CancellationToken, Task> messageHandler, string parameterPath, IPAddress ipAddress)
        {
            _messageHandler = messageHandler;
            _ipAddress = ipAddress;
            _config = new Ism7Config(parameterPath);
            _pipe = new Pipe();
        }

        public async Task RunAsync(string password, CancellationToken cancellationToken)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(_ipAddress, 9092, cancellationToken);
            var certificate = new X509Certificate2(Resources.client);
            using (var ssl = new SslStream(tcp.GetStream(), false, (a, b, c, d) => true))
            {
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "ism7.server",
                    ClientCertificates = new X509Certificate2Collection(certificate),
                };
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        sslOptions.CipherSuitesPolicy = new CipherSuitesPolicy(new[]
                        {
                            TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA256
                        });
                    }
                    catch (PlatformNotSupportedException)
                    {
                        //older linux or mac https://github.com/dotnet/runtime/issues/33649
                    }
                }
                await ssl.AuthenticateAsClientAsync(sslOptions, cancellationToken);
                var fillPipeTask = FillPipeAsync(ssl, _pipe.Writer, cancellationToken);
                var readPipeTask = ReadPipeAsync(_pipe.Reader, cancellationToken);
                await AuthenticateAsync(ssl, password, cancellationToken);
                await Task.WhenAny(fillPipeTask, readPipeTask);
            }
        }

        private async Task FillPipeAsync(Stream connection, PipeWriter target, CancellationToken cancellationToken)
        {
            const int bufferSize = 512;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var buffer = target.GetMemory(bufferSize);
                    var read = await connection.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    target.Advance(read);
                    var result = await target.FlushAsync(cancellationToken);
                    if (result.IsCanceled || result.IsCompleted)
                        break;
                }
                await target.CompleteAsync();
            }
            catch (Exception ex)
            {
                await target.CompleteAsync(ex);
            }
        }

        private async Task ReadPipeAsync(PipeReader source, CancellationToken cancellationToken)
        {
            try
            {
                var header = new byte[6];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await source.ReadAsync(cancellationToken);
                    var buffer = result.Buffer;
                    while (buffer.Length >= 6)
                    {
                        var size = buffer.Slice(0, 6);
                        size.CopyTo(header);
                        var length = BinaryPrimitives.ReadInt32BigEndian(header);
                        if (buffer.Length < length) break;
                        var type = (PayloadType)BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(4));
                        var xmlBuffer = buffer.Slice(6, length);
                        if (EnableDebug)
                        {
                            var xml = Encoding.UTF8.GetString(xmlBuffer);
                            Console.WriteLine($"< {xml}");
                        }
                        var response = Deserialize(type, new ReadOnlySequenceStream(xmlBuffer));
                        await _dispatcher.DispatchAsync(response, cancellationToken);
                        buffer = buffer.Slice(xmlBuffer.End);
                        
                    }
                    source.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
                await source.CompleteAsync();
            }
            catch (Exception ex)
            {
                await source.CompleteAsync(ex);
            }
        }
        
        private async Task SubscribeAsync(Stream connection, string busAddress, CancellationToken cancellationToken)
        {
            var device = _devices[busAddress];
            {
                var ids = _config.GetTelegramIdsForDevice(device.Ba);
                var bundleId = NextBundleId();
                _dispatcher.Subscribe(x=>x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp)x).BundleId == bundleId, OnPushResponseAsync);
                await SendAsync(connection, new TelegramBundleReq
                {
                    AbortOnError = false,
                    BundleId = bundleId,
                    GatewayId = "1",
                    TelegramBundleType = TelegramBundleType.push,
                    InfoReadTelegrams = ids.Select(x=>new InfoRead
                    {
                        BusAddress = device.Ba,
                        InfoNumber = x,
                        Intervall = 60
                        
                    }).ToList()
                }, cancellationToken);
            }
        }

        private async Task OnPushResponseAsync(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp) response;
            if (!String.IsNullOrEmpty(resp.Errormsg))
                throw new InvalidDataException(resp.Errormsg);
            if (resp.State != TelegrResponseState.OK)
                throw new InvalidDataException($"unexpected state '{resp.State}");
            
            var datapoints = _config.ProcessData(resp.Telegrams.Where(x => x.State == TelegrResponseState.OK));
            foreach (var datapoint in datapoints)
            {
                await _messageHandler(datapoint, cancellationToken);
            }
        }

        private async Task LoadInitialValuesAsync(Stream connection, CancellationToken cancellationToken)
        {
            foreach (var device in _devices.Values)
            {
                _config.AddDevice(_ipAddress.ToString(), device.Ba, device.DeviceId, device.SoftwareNumber);
                var ids = _config.GetTelegramIdsForDevice(device.Ba);
                var bundleId = NextBundleId();
                _dispatcher.SubscribeOnce(
                    x => x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp) x).BundleId == bundleId,
                    (r, c) => OnInitialValuesAsync(r, connection, c));
                await SendAsync(connection, new TelegramBundleReq
                {
                    AbortOnError = false,
                    BundleId = bundleId,
                    GatewayId = "1",
                    TelegramBundleType = TelegramBundleType.pull,
                    InfoReadTelegrams = ids.Select(x=>new InfoRead
                    {
                        BusAddress = device.Ba,
                        InfoNumber = x,
                    }).ToList()
                }, cancellationToken);
            }
        }

        private async Task OnInitialValuesAsync(IResponse response, Stream connection, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp) response;
            if (!String.IsNullOrEmpty(resp.Errormsg))
                throw new InvalidDataException(resp.Errormsg);
            if (resp.State != TelegrResponseState.OK)
                throw new InvalidDataException($"unexpected state '{resp.State}");
            if (resp.Telegrams.Any())
            {
                var datapoints = _config.ProcessData(resp.Telegrams.Where(x => x.State == TelegrResponseState.OK));
                foreach (var datapoint in datapoints)
                {
                    await _messageHandler(datapoint, cancellationToken);
                }
                var busAddress = resp.Telegrams.Select(x => x.BusAddress).First();
                await SubscribeAsync(connection, busAddress, cancellationToken);
            }
        }

        private async Task GetConfigAsync(Stream connection, LoginResp session, CancellationToken cancellationToken)
        {
            _dispatcher.SubscribeOnce(x => x.MessageType == PayloadType.SystemconfigResp,
                (r, c) => OnSystemConfigAsync(r, connection, c));
            await SendAsync(connection, new SystemconfigReq {Sid = session.Sid}, cancellationToken);
        }

        private Task OnSystemConfigAsync(IResponse response, Stream connection, CancellationToken cancellationToken)
        {
            var resp = (SystemconfigResp)response;
            foreach (var device in resp.BusConfig.Devices)
            {
                _devices.AddOrUpdate(device.Ba, device, (k, o) => device);
            }
            return LoadInitialValuesAsync(connection, cancellationToken);
        }

        private ValueTask AuthenticateAsync(Stream connection, string password, CancellationToken cancellationToken)
        {
            _dispatcher.SubscribeOnce(x => x.MessageType == PayloadType.DirectLogonResp,
                (r, c) => OnAuthenticateAsync(r, connection, c));
            return SendAsync(connection, new LoginReq {Password = password}, cancellationToken);
        }

        private Task OnAuthenticateAsync(IResponse response, Stream connection, CancellationToken cancellationToken)
        {
            var resp = (LoginResp)response;
            if (resp.State != LoginState.ok)
                throw new InvalidDataException("invalid login state");
            return GetConfigAsync(connection, resp, cancellationToken);
        }

        private ValueTask SendAsync<T>(Stream connection, T payload, CancellationToken cancellationToken) where T:IPayload
        {
            var data = Serialize(payload);
            if (EnableDebug)
                Console.WriteLine($"> {data}");
            var length = Encoding.UTF8.GetByteCount(data);
            var buffer = new byte[length + 6];
            BinaryPrimitives.WriteInt32BigEndian(buffer, length);
            BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(4), (short) payload.Type);
            Encoding.UTF8.GetBytes(data, buffer.AsSpan(6));
            return connection.WriteAsync(buffer, cancellationToken);
        }

        private string NextBundleId()
        {
            var id = Interlocked.Increment(ref _nextBundleId);
            return id.ToString();
        }

        private IResponse Deserialize(PayloadType type, Stream data)
        {
            switch (type)
            {
                case PayloadType.DirectLogonResp:
                    return (IResponse) GetSerializer<LoginResp>().Deserialize(data);
                case PayloadType.SystemconfigResp:
                    return (IResponse) GetSerializer<SystemconfigResp>().Deserialize(data);
                case PayloadType.TgrBundleResp:
                    return (IResponse) GetSerializer<TelegramBundleResp>().Deserialize(data);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private string Serialize<T>(T request)
        {
            using var sw = new StringWriter();
            var xmlWriter = XmlWriter.Create(sw, new XmlWriterSettings {Indent = false});
            var serializer = GetSerializer<T>();
            serializer.Serialize(xmlWriter, request);
            sw.Flush();
            return sw.ToString();
        }

        private XmlSerializer GetSerializer<T>()
        {
            return _serializers.GetOrAdd(typeof(T), x => new XmlSerializer(x));
        }
    }
}