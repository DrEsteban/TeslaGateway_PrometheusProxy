using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;
using SolarGateway_PrometheusProxy.Exceptions;
using SolarGateway_PrometheusProxy.Models;

namespace SolarGateway_PrometheusProxy.Services;

/// <summary>
/// Provides metrics from a Tesla Gateway and saves them to a Prometheus <see cref="CollectorRegistry"/>.
/// </summary>
public partial class TeslaGatewayMetricsService : MetricsServiceBase
{
    private readonly TeslaLoginRequest _loginRequest;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _loginCacheLength;

    public TeslaGatewayMetricsService(
        IOptions<TeslaLoginRequest> loginRequest,
        ILogger<TeslaGatewayMetricsService> logger,
        IMemoryCache cache,
        IHttpClientFactory clientFactory,
        IOptions<TeslaConfiguration> configuration)
        : base(clientFactory.CreateClient(nameof(TeslaGatewayMetricsService)), logger)
    {
        _loginRequest = loginRequest.Value;
        _cache = cache;
        _loginCacheLength = TimeSpan.FromMinutes(configuration.Value.LoginCacheMinutes);
    }

    protected override string MetricCategory => "tesla_gateway";

    /// <summary>
    /// Collects metrics from the Tesla Gateway saves to the Prometheus <see cref="CollectorRegistry"/>.
    /// </summary>
    /// <exception cref="MetricRequestFailedException">Thrown when the Tesla Gateway returns an unexpected response.</exception>
    /// <exception cref="Exception"></exception>
    public override async Task CollectMetricsAsync(CollectorRegistry collectorRegistry, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        bool loginCached = true;

        // Get a cached auth token
        var loginResponse = await _cache.GetOrCreateAsync("gateway_token", async e =>
        {
            loginCached = false;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login/Basic");
            request.Content = JsonContent.Create(_loginRequest);
            using var response = await _client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string err = $"Got {response.StatusCode} calling login endpoint: {responseContent}";
                _logger.LogError(err);
                throw new MetricRequestFailedException(err);
            }

            e.AbsoluteExpirationRelativeToNow = _loginCacheLength;
            return JsonSerializer.Deserialize<TeslaLoginResponse>(responseContent);
        });

        if (string.IsNullOrEmpty(loginResponse?.Token))
        {
            string err = $"Failed to parse {nameof(TeslaLoginResponse)} for valid token";
            _logger.LogError(err);
            throw new Exception(err);
        }

        // Get metrics in parallel
        var results = await Task.WhenAll(
            this.PullMeterAggregates(collectorRegistry, loginResponse, cancellationToken),
            this.PullPowerwallPercentage(collectorRegistry, loginResponse, cancellationToken),
            this.PullSiteInfo(collectorRegistry, loginResponse, cancellationToken),
            this.PullStatus(collectorRegistry, loginResponse, cancellationToken),
            this.PullOperation(collectorRegistry, loginResponse, cancellationToken));
        if (!results.All(r => r))
        {
            throw new MetricRequestFailedException($"Failed to pull {results.Count(r => !r)}/{results.Length} endpoints on Tesla gateway");
        }

        base.SetRequestDurationMetric(collectorRegistry, loginCached, sw.Elapsed);
    }

    private async Task<bool> PullMeterAggregates(CollectorRegistry registry, TeslaLoginResponse loginResponse, CancellationToken cancellationToken)
    {
        using var metricsDocument = await base.CallMetricEndpointAsync("/api/meters/aggregates", loginResponse.ToAuthenticationHeader, cancellationToken);
        if (metricsDocument is null)
        {
            return false;
        }

        foreach (var category in metricsDocument.RootElement.EnumerateObject())
        {
            foreach (var metric in category.Value.EnumerateObject())
            {
                switch (metric.Value.ValueKind)
                {
                    case JsonValueKind.Number:
                        base.CreateGauge(registry, category.Name, metric.Name).Set(metric.Value.GetDouble());
                        break;
                    case JsonValueKind.String:
                        // Assumed to be DateTime
                        if (DateTimeOffset.TryParse(metric.Value.GetString(), out var date))
                        {
                            base.CreateGauge(registry, category.Name, metric.Name).Set(date.ToUnixTimeSeconds());
                        }

                        break;
                    default:
                        _logger.LogWarning($"Unsupported ValueKind: {metric.Value.ValueKind}");
                        break;
                }
            }
        }

        return true;
    }

    private async Task<bool> PullPowerwallPercentage(CollectorRegistry registry, TeslaLoginResponse loginResponse, CancellationToken cancellationToken)
    {
        using var metricsDocument = await base.CallMetricEndpointAsync("/api/system_status/soe", loginResponse.ToAuthenticationHeader, cancellationToken);
        if (metricsDocument is null)
        {
            return false;
        }

        base.CreateGauge(registry, "powerwall", "percentage").Set(metricsDocument.RootElement.GetProperty("percentage").GetDouble());
        return true;
    }

    private async Task<bool> PullSiteInfo(CollectorRegistry registry, TeslaLoginResponse loginResponse, CancellationToken cancellationToken)
    {
        using var metricsDocument = await base.CallMetricEndpointAsync("/api/site_info", loginResponse.ToAuthenticationHeader, cancellationToken);
        if (metricsDocument is null)
        {
            return false;
        }

        foreach (var metric in metricsDocument.RootElement.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Number))
        {
            base.CreateGauge(registry, "siteinfo", metric.Name).Set(metric.Value.GetDouble());
        }

        return true;
    }

    [GeneratedRegex(@"^(?<hours>[0-9]*)h(?<minutes>[0-9]*)m(?<seconds>[0-9]*)(\.[0-9]*s)?$")]
    private static partial Regex UpTimeRegex();

    private async Task<bool> PullStatus(CollectorRegistry registry, TeslaLoginResponse loginResponse, CancellationToken cancellationToken)
    {
        using var metricsDocument = await base.CallMetricEndpointAsync("/api/status", loginResponse.ToAuthenticationHeader, cancellationToken);
        if (metricsDocument is null)
        {
            return false;
        }

        if (DateTimeOffset.TryParse(metricsDocument.RootElement.GetProperty("start_time").GetString(), out var startTime))
        {
            base.CreateGauge(registry, "status", "start_time").Set(startTime.ToUnixTimeSeconds());
        }

        var match = UpTimeRegex().Match(metricsDocument.RootElement.GetProperty("up_time_seconds").GetString() ?? string.Empty);
        if (match.Success)
        {
            int hours = int.Parse(match.Groups["hours"].Value);
            int minutes = int.Parse(match.Groups["minutes"].Value);
            int seconds = int.Parse(match.Groups["seconds"].Value);
            var timeSpan = new TimeSpan(hours, minutes, seconds);
            base.CreateGauge(registry, "status", "up_time_seconds").Set(timeSpan.TotalSeconds);
        }

        return true;
    }

    private async Task<bool> PullOperation(CollectorRegistry registry, TeslaLoginResponse loginResponse, CancellationToken cancellationToken)
    {
        using var metricsDocument = await base.CallMetricEndpointAsync("/api/operation", loginResponse.ToAuthenticationHeader, cancellationToken);
        if (metricsDocument is null)
        {
            return false;
        }

        base.CreateGauge(registry, "operation", "backup_reserve_percent").Set(metricsDocument.RootElement.GetProperty("backup_reserve_percent").GetDouble());

        string? realMode = metricsDocument.RootElement.GetProperty("real_mode").GetString();
        Func<string, Gauge.Child> GetModeGauge = (mode) => base.CreateGauge(registry, "operation", "mode", KeyValuePair.Create("mode", mode));

        const string selfConsumption = "self_consumption", autonomous = "autonomous", backup = "backup";
        GetModeGauge(selfConsumption).Set(realMode == selfConsumption ? 1 : 0);
        GetModeGauge(autonomous).Set(realMode == autonomous ? 1 : 0);
        GetModeGauge(backup).Set(realMode == backup ? 1 : 0);

        return true;
    }
}