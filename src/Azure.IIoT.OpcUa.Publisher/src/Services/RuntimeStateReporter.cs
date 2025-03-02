﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Azure.IIoT.OpcUa.Publisher.Services
{
    using Azure.IIoT.OpcUa.Publisher;
    using Azure.IIoT.OpcUa.Publisher.Models;
    using Azure.IIoT.OpcUa.Encoders;
    using Furly.Azure.IoT.Edge;
    using Furly.Azure.IoT.Edge.Services;
    using Furly.Extensions.Messaging;
    using Furly.Extensions.Serializers;
    using Furly.Extensions.Storage;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Diagnostics.Metrics;
    using System.Runtime.InteropServices;
    using System.Globalization;
    using System.Net.Sockets;

    /// <summary>
    /// This class manages reporting of runtime state.
    /// </summary>
    public sealed class RuntimeStateReporter : IRuntimeStateReporter,
        IApiKeyProvider, ISslCertProvider, IDisposable
    {
        /// <inheritdoc/>
        public string? ApiKey { get; private set; }

        /// <inheritdoc/>
        public X509Certificate2? Certificate { get; private set; }

        /// <summary>
        /// Constructor for runtime state reporter.
        /// </summary>
        /// <param name="events"></param>
        /// <param name="serializer"></param>
        /// <param name="stores"></param>
        /// <param name="options"></param>
        /// <param name="collector"></param>
        /// <param name="logger"></param>
        /// <param name="metrics"></param>
        /// <param name="identity"></param>
        /// <param name="workload"></param>
        public RuntimeStateReporter(IEnumerable<IEventClient> events,
            IJsonSerializer serializer, IEnumerable<IKeyValueStore> stores,
            IOptions<PublisherOptions> options, IDiagnosticCollector collector,
            ILogger<RuntimeStateReporter> logger, IMetricsContext? metrics = null,
            IIoTEdgeDeviceIdentity? identity = null, IIoTEdgeWorkloadApi? workload = null)
        {
            _serializer = serializer ??
                throw new ArgumentNullException(nameof(serializer));
            _options = options ??
                throw new ArgumentNullException(nameof(options));
            _logger = logger ??
                throw new ArgumentNullException(nameof(logger));
            _collector = collector ??
                throw new ArgumentNullException(nameof(collector));
            _metrics = metrics ?? IMetricsContext.Empty;
            _workload = workload;
            _identity = identity;
            _renewalTimer = new Timer(OnRenewExpiredCertificateAsync);

            ArgumentNullException.ThrowIfNull(stores);
            ArgumentNullException.ThrowIfNull(events);

            _events = events.Reverse().ToList();
            _stores = stores.Reverse().ToList();
            if (_stores.Count == 0)
            {
                throw new ArgumentException("No key value stores configured.",
                    nameof(stores));
            }
            _runtimeState = RuntimeStateEventType.RestartAnnouncement;
            _topicCache = new ConcurrentDictionary<string, string>();

            _diagnosticInterval = options.Value.DiagnosticsInterval ?? TimeSpan.Zero;
            _diagnostics = options.Value.DiagnosticsTarget ?? PublisherDiagnosticTargetType.Logger;
            if (_diagnosticInterval == TimeSpan.Zero)
            {
                _diagnosticInterval = Timeout.InfiniteTimeSpan;
            }

            _cts = new CancellationTokenSource();
            _diagnosticsOutputTimer = new PeriodicTimer(_diagnosticInterval);
            _publisher = DiagnosticsOutputTimerAsync(_cts.Token);

            InitializeMetrics();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try
            {
                _runtimeState = RuntimeStateEventType.Stopped;
                _cts.Cancel();
                _publisher.GetAwaiter().GetResult();
                Certificate?.Dispose();
            }
            finally
            {
                _renewalTimer.Dispose();
                _meter.Dispose();
                _diagnosticsOutputTimer.Dispose();
                _publisher = Task.CompletedTask;
                _cts.Dispose();
            }
        }

        /// <inheritdoc/>
        public async ValueTask SendRestartAnnouncementAsync(CancellationToken ct)
        {
            // Set runtime state in state stores
            foreach (var store in _stores)
            {
                store.State[OpcUa.Constants.TwinPropertySiteKey] =
                    _options.Value.Site;
                store.State[OpcUa.Constants.TwinPropertyTypeKey] =
                    OpcUa.Constants.EntityTypePublisher;
                store.State[OpcUa.Constants.TwinPropertyVersionKey] =
                    GetType().Assembly.GetReleaseVersion().ToString();
            }

            await UpdateApiKeyAndCertificateAsync().ConfigureAwait(false);

            if (_options.Value.EnableRuntimeStateReporting ?? false)
            {
                var body = new RuntimeStateEventModel
                {
                    TimestampUtc = DateTime.UtcNow,
                    MessageVersion = 1,
                    MessageType = RuntimeStateEventType.RestartAnnouncement,
                    PublisherId = _options.Value.PublisherId,
                    Site = _options.Value.Site,
                    DeviceId = _identity?.DeviceId,
                    ModuleId = _identity?.ModuleId,
                    Version = GetType().Assembly.GetReleaseVersion().ToString()
                };

                await SendRuntimeStateEvent(body, ct).ConfigureAwait(false);
                _logger.LogInformation("Restart announcement sent successfully.");
            }

            _runtimeState = RuntimeStateEventType.Running;
        }

        /// <summary>
        /// Update cached api key
        /// </summary>
        private async Task UpdateApiKeyAndCertificateAsync()
        {
            var apiKeyStore = _stores.Find(s => s.State.TryGetValue(
                OpcUa.Constants.TwinPropertyApiKeyKey, out var key) && key.IsString);
            if (apiKeyStore != null)
            {
                ApiKey = (string?)apiKeyStore.State[OpcUa.Constants.TwinPropertyApiKeyKey];
                _logger.LogInformation("Api Key exists in {Store} store...", apiKeyStore.Name);
            }
            else
            {
                Debug.Assert(_stores.Count > 0);
                _logger.LogInformation("Generating new Api Key in {Store} store...",
                    _stores[0].Name);
                ApiKey = RandomNumberGenerator.GetBytes(20).ToBase64String();
                _stores[0].State.Add(OpcUa.Constants.TwinPropertyApiKeyKey, ApiKey);
            }

            var dnsName = Dns.GetHostName();

            // The certificate must be in the same store as the api key or else we generate a new one.
            if (!(_options.Value.RenewTlsCertificateOnStartup ?? false) &&
                apiKeyStore != null &&
                apiKeyStore.State.TryGetValue(OpcUa.Constants.TwinPropertyCertificateKey,
                    out var cert) && cert.IsBytes)
            {
                try
                {
                    // Load certificate
                    Certificate?.Dispose();
                    Certificate = new X509Certificate2((byte[])cert!, ApiKey);
                    var now = DateTime.UtcNow.AddDays(1);
                    if (now < Certificate.NotAfter && Certificate.HasPrivateKey &&
                        Certificate.SubjectName.EnumerateRelativeDistinguishedNames()
                            .Any(a => a.GetSingleElementValue() == dnsName))
                    {
                        var renewalAfter = Certificate.NotAfter - now;
                        _logger.LogInformation(
                            "Using valid Certificate found in {Store} store (renewal in {Duration})...",
                            apiKeyStore.Name, renewalAfter);
                        _renewalTimer.Change(renewalAfter, Timeout.InfiniteTimeSpan);
                        // Done
                        return;
                    }
                    _logger.LogInformation(
                        "Certificate found in {Store} store has expired. Generate new...",
                        apiKeyStore.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provided Certificate invalid.");
                }
            }

            // Create new certificate
            var nowOffset = DateTimeOffset.UtcNow;
            var expiration = nowOffset.AddDays(kCertificateLifetimeDays);

            Certificate?.Dispose();
            Certificate = null;
            if (_workload != null)
            {
                try
                {
                    var certificates = await _workload.CreateServerCertificateAsync(
                        dnsName, expiration.Date).ConfigureAwait(false);

                    Debug.Assert(certificates.Count > 0);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using (var certificate = certificates[0])
                        {
                            //
                            // https://github.com/dotnet/runtime/issues/45680)
                            // On Windows the certificate in 'result' gives an error
                            // when used with kestrel: "No credentials are available"
                            //
                            Certificate = new X509Certificate2(
                                certificate.Export(X509ContentType.Pkcs12));
                        }
                    }
                    else
                    {
                        Certificate = certificates[0];
                    }

                    if (!Certificate.HasPrivateKey)
                    {
                        Certificate.Dispose();
                        Certificate = null;
                        _logger.LogWarning(
                            "Failed to get certificate with private key using workload API.");
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Using server certificate with private key from workload API...");
                    }
                }
                catch (NotSupportedException nse)
                {
                    _logger.LogWarning("Not supported: {Message}. " +
                        "Unable to use workload API to obtain the certificate!", nse.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create certificate using workload API.");
                }
            }
            if (Certificate == null)
            {
                using var ecdsa = ECDsa.Create();
                var req = new CertificateRequest("DC=" + dnsName, ecdsa, HashAlgorithmName.SHA256);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(dnsName);
                var altDns = _identity?.ModuleId ?? _identity?.DeviceId;
                if (!string.IsNullOrEmpty(altDns) &&
                    !string.Equals(altDns, dnsName, StringComparison.OrdinalIgnoreCase))
                {
                    san.AddDnsName(altDns);
                }
                req.CertificateExtensions.Add(san.Build());
                Certificate = req.CreateSelfSigned(DateTimeOffset.Now, expiration);
                Debug.Assert(Certificate.HasPrivateKey);
                _logger.LogInformation("Created self-signed ECC server certificate...");
            }

            Debug.Assert(_stores.Count > 0);
            Debug.Assert(ApiKey != null);
            apiKeyStore ??= _stores[0];

            var pfxCertificate = Certificate.Export(X509ContentType.Pfx, ApiKey);
            apiKeyStore.State.AddOrUpdate(OpcUa.Constants.TwinPropertyCertificateKey, pfxCertificate);

            var renewalDuration = Certificate.NotAfter - nowOffset.Date - TimeSpan.FromDays(1);
            _renewalTimer.Change(renewalDuration, Timeout.InfiniteTimeSpan);

            _logger.LogInformation(
                "Stored new Certificate in {Store} store (and scheduled renewal after {Duration}).",
                apiKeyStore.Name, renewalDuration);
            _certificateRenewals++;
        }

        /// <summary>
        /// Renew certifiate
        /// </summary>
        /// <param name="state"></param>
        private async void OnRenewExpiredCertificateAsync(object? state)
        {
            try
            {
                await UpdateApiKeyAndCertificateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Retry
                _logger.LogCritical(ex, "Failed to renew certificate - retrying in 1 hour...");
                _renewalTimer.Change(TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Send runtime state events
        /// </summary>
        /// <param name="runtimeStateEvent"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task SendRuntimeStateEvent(RuntimeStateEventModel runtimeStateEvent,
            CancellationToken ct)
        {
            await Task.WhenAll(_events.Select(SendEventAsync)).ConfigureAwait(false);

            async Task SendEventAsync(IEventClient events)
            {
                try
                {
                    await events.SendEventAsync(new TopicBuilder(_options).EventsTopic,
                        _serializer.SerializeToMemory(runtimeStateEvent), _serializer.MimeType,
                        Encoding.UTF8.WebName, configure: eventMessage =>
                        {
                            eventMessage
                                .SetRetain(true)
                                .AddProperty(OpcUa.Constants.MessagePropertySchemaKey,
                                    MessageSchemaTypes.RuntimeStateMessage);
                            if (_options.Value.RuntimeStateRoutingInfo != null)
                            {
                                eventMessage.AddProperty(OpcUa.Constants.MessagePropertyRoutingKey,
                                    _options.Value.RuntimeStateRoutingInfo);
                            }
                        }, ct).ConfigureAwait(false);

                    _logger.LogInformation("{Event} sent via {Transport}.", runtimeStateEvent,
                        events.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed sending {MessageType} runtime state event through {Transport}.",
                        runtimeStateEvent.MessageType, events.Name);
                }
            }
        }

        /// <summary>
        /// Diagnostics timer to dump out all diagnostics
        /// </summary>
        /// <param name="ct"></param>
        private async Task DiagnosticsOutputTimerAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _diagnosticsOutputTimer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                    var diagnostics = _collector.EnumerateDiagnostics();

                    switch (_diagnostics)
                    {
                        case PublisherDiagnosticTargetType.Events:
                            await SendDiagnosticsAsync(diagnostics, ct).ConfigureAwait(false);
                            break;
                        // TODO: case PublisherDiagnosticTargetType.PubSub:
                        // TODO:     break;
                        default:
                            WriteDiagnosticsToConsole(diagnostics);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during diagnostics processing.");
                }
            }
        }

        /// <summary>
        /// Send diagnostics
        /// </summary>
        /// <param name="diagnostics"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async ValueTask SendDiagnosticsAsync(
            IEnumerable<(string, WriterGroupDiagnosticModel)> diagnostics, CancellationToken ct)
        {
            foreach (var (writerGroupId, info) in diagnostics)
            {
                var diagnosticsTopic = _topicCache.GetOrAdd(writerGroupId,
                    id => new TopicBuilder(_options, new Dictionary<string, string>
                    {
                        [PublisherConfig.DataSetWriterGroupVariableName] =
                            id ?? Constants.DefaultWriterGroupId
                        // ...
                    }).DiagnosticsTopic);

                await Task.WhenAll(_events.Select(SendEventAsync)).ConfigureAwait(false);

                async Task SendEventAsync(IEventClient events)
                {
                    try
                    {
                        await events.SendEventAsync(diagnosticsTopic,
                            _serializer.SerializeToMemory(info), _serializer.MimeType,
                            Encoding.UTF8.WebName, configure: eventMessage =>
                            {
                                eventMessage
                                    .SetRetain(true)
                                    .SetTtl(_diagnosticInterval + TimeSpan.FromSeconds(10))
                                    .AddProperty(OpcUa.Constants.MessagePropertySchemaKey,
                                        MessageSchemaTypes.WriterGroupDiagnosticsMessage);
                                if (_options.Value.RuntimeStateRoutingInfo != null)
                                {
                                    eventMessage.AddProperty(OpcUa.Constants.MessagePropertyRoutingKey,
                                        _options.Value.RuntimeStateRoutingInfo);
                                }
                            }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed sending Diagnostics event through {Transport}.", events.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Format diagnostics to console
        /// </summary>
        /// <param name="diagnostics"></param>
        private void WriteDiagnosticsToConsole(IEnumerable<(string, WriterGroupDiagnosticModel)> diagnostics)
        {
            var builder = new StringBuilder();
            foreach (var (writerGroupId, info) in diagnostics)
            {
                builder = Append(builder, writerGroupId, info);
            }

            if (builder.Length > 0)
            {
                Console.Out.WriteLine(builder.ToString());
            }

            StringBuilder Append(StringBuilder builder, string writerGroupId,
                WriterGroupDiagnosticModel info)
            {
                var ingestionDuration = info.IngestionDuration;
                var valueChangesPerSec = info.IngressValueChanges / ingestionDuration.TotalSeconds;
                var dataChangesPerSec = info.IngressDataChanges / ingestionDuration.TotalSeconds;
                var dataChangesLastMin = info.IngressDataChangesInLastMinute
                    .ToString("D2", CultureInfo.CurrentCulture);
                var valueChangesPerSecLastMin = info.IngressValueChangesInLastMinute /
                    Math.Min(ingestionDuration.TotalSeconds, 60d);
                var dataChangesPerSecLastMin = info.IngressDataChangesInLastMinute /
                    Math.Min(ingestionDuration.TotalSeconds, 60d);
                var version = GetType().Assembly.GetReleaseVersion().ToString();

                var dataChangesPerSecFormatted = info.IngressDataChanges > 0 && ingestionDuration.TotalSeconds > 0
        ? $"(All time ~{dataChangesPerSec:0.##}/s; {dataChangesLastMin} in last 60s ~{dataChangesPerSecLastMin:0.##}/s)"
                    : string.Empty;
                var valueChangesPerSecFormatted = info.IngressValueChanges > 0 && ingestionDuration.TotalSeconds > 0
        ? $"(All time ~{valueChangesPerSec:0.##}/s; {dataChangesLastMin} in last 60s ~{valueChangesPerSecLastMin:0.##}/s)"
                    : string.Empty;
                var sentMessagesPerSecFormatted = info.OutgressIoTMessageCount > 0 && ingestionDuration.TotalSeconds > 0
        ? $"({info.SentMessagesPerSec:0.##}/s)" : "";

                return builder.AppendLine()
                    .Append("  DIAGNOSTICS INFORMATION for          : ")
                        .Append(writerGroupId).Append(" (OPC Publisher ").Append(version)
                        .AppendLine(")")
                    .Append("  # Time                               : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:O}", info.Timestamp)
                        .AppendLine()
                    .Append("  # Ingestion duration                 : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:dd\\:hh\\:mm\\:ss}", ingestionDuration)
                        .AppendLine(" (dd:hh:mm:ss)")
                    .Append("  # Ingress DataChanges (from OPC)     : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressDataChanges)
                        .Append(' ').AppendLine(dataChangesPerSecFormatted)
                    .Append("  # Ingress ValueChanges (from OPC)    : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressValueChanges).Append(' ')
                        .AppendLine(valueChangesPerSecFormatted)
                    .Append("  # of which are Heartbeats            : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressHeartbeats)
                        .AppendLine()
                    .Append("  # of which are Cyclic reads          : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressCyclicReads)
                        .AppendLine()
                    .Append("  # Ingress EventData (from OPC)       : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressEventNotifications)
                        .AppendLine()
                    .Append("  # Ingress Events (from OPC)          : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.IngressEvents)
                        .AppendLine()
                    .Append("  # Ingress BatchBlock buffer size     : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.IngressBatchBlockBufferSize)
                        .AppendLine()
                    .Append("  # Encoding Block input/output size   : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.EncodingBlockInputSize).Append(" | ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0:0}", info.EncodingBlockOutputSize).AppendLine()
                    .Append("  # Encoder Notifications processed    : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.EncoderNotificationsProcessed)
                        .AppendLine()
                    .Append("  # Encoder Notifications dropped      : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.EncoderNotificationsDropped)
                        .AppendLine()
                    .Append("  # Encoder IoT Messages processed     : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.EncoderIoTMessagesProcessed)
                        .AppendLine()
                    .Append("  # Encoder avg Notifications/Message  : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.EncoderAvgNotificationsMessage)
                        .AppendLine()
                    .Append("  # Encoder worst Message split ratio  : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0.#}", info.EncoderMaxMessageSplitRatio)
                        .AppendLine()
                    .Append("  # Encoder avg IoT Message body size  : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.EncoderAvgIoTMessageBodySize)
                        .AppendLine()
                    .Append("  # Encoder avg IoT Chunk (4 KB) usage : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0.#}", info.EncoderAvgIoTChunkUsage)
                        .AppendLine()
                    .Append("  # Estimated IoT Chunks (4 KB) per day: ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.EstimatedIoTChunksPerDay)
                        .AppendLine()
                    .Append("  # Outgress input buffer count        : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.OutgressInputBufferCount)
                        .AppendLine()
                    .Append("  # Outgress input buffer dropped      : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.OutgressInputBufferDropped)
                        .AppendLine()
                    .Append("  # Outgress IoT message count         : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:n0}", info.OutgressIoTMessageCount)
                        .Append(' ').AppendLine(sentMessagesPerSecFormatted)
                    .Append("  # Connection retries                 : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.ConnectionRetries)
                        .AppendLine()
                    .Append("  # Opc endpoint connected?            : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.OpcEndpointConnected)
                        .AppendLine()
                    .Append("  # Monitored Opc nodes succeeded count: ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.MonitoredOpcNodesSucceededCount)
                        .AppendLine()
                    .Append("  # Monitored Opc nodes failed count   : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.MonitoredOpcNodesFailedCount)
                        .AppendLine()
                    .Append("  # Subscriptions count                : ")
                        .AppendFormat(CultureInfo.CurrentCulture, "{0,14:0}", info.NumberOfSubscriptions)
                        .AppendLine()
                    ;
            }
        }

        /// <summary>
        /// Create observable metrics
        /// </summary>
        private void InitializeMetrics()
        {
            _meter.CreateObservableGauge("iiot_edge_publisher_module_start",
                () => new Measurement<int>(_runtimeState == RuntimeStateEventType.RestartAnnouncement ? 0 : 1,
                _metrics.TagList), "Count", "Publisher module started.");
            _meter.CreateObservableGauge("iiot_edge_publisher_module_state",
                () => new Measurement<int>((int)_runtimeState,
                _metrics.TagList), "State", "Publisher module runtime state.");
            _meter.CreateObservableCounter("iiot_edge_publisher_certificate_renewal_count",
                () => new Measurement<int>(_certificateRenewals,
                _metrics.TagList), "Count", "Publisher certificate renewals.");
        }

        private const int kCertificateLifetimeDays = 30;
        private readonly ConcurrentDictionary<string, string> _topicCache;
        private readonly ILogger _logger;
        private readonly IIoTEdgeDeviceIdentity? _identity;
        private readonly IDiagnosticCollector _collector;
        private readonly IIoTEdgeWorkloadApi? _workload;
        private readonly Timer _renewalTimer;
        private readonly IJsonSerializer _serializer;
        private readonly IOptions<PublisherOptions> _options;
        private readonly List<IEventClient> _events;
        private readonly List<IKeyValueStore> _stores;
        private readonly Meter _meter = Diagnostics.NewMeter();
        private readonly IMetricsContext _metrics;
        private readonly CancellationTokenSource _cts;
        private readonly PeriodicTimer _diagnosticsOutputTimer;
        private readonly TimeSpan _diagnosticInterval;
        private readonly PublisherDiagnosticTargetType _diagnostics;
        private RuntimeStateEventType _runtimeState;
        private Task _publisher;
        private int _certificateRenewals;
    }
}
