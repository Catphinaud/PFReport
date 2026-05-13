using Dalamud.Game.Gui.PartyFinder.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PFReport;

internal sealed class PartyFinderLogger : IDisposable
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan FailurePauseDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan[] FailureRetryDelays =
    {
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Configuration configuration;
    private readonly HttpClient httpClient = new();
    private readonly object sync = new();
    private readonly List<PartyFinderLogListing> pendingListings = new();
    private readonly HashSet<ulong> pendingHashes = new();
    private readonly HashSet<ulong> inFlightHashes = new();
    private readonly HashSet<ulong> sentHashes = new();
    private readonly Queue<ulong> sentHashOrder = new();
    private readonly Timer flushTimer;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();

    private int consecutiveFailures;
    private DateTimeOffset? pausedUntilUtc;
    private bool disposed;

    public PartyFinderLogger(Configuration configuration)
    {
        this.configuration = configuration;
        httpClient.Timeout = TimeSpan.FromSeconds(6);
        flushTimer = new Timer(_ => _ = FlushPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int PendingCount
    {
        get
        {
            lock (sync)
                return pendingListings.Count;
        }
    }

    public int SentHashCount
    {
        get
        {
            lock (sync)
                return sentHashes.Count;
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (sync)
                return inFlightHashes.Count;
        }
    }

    public void Observe(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (!configuration.LoggingEnabled || !TryGetEndpointUri(out _) || IsPaused())
            return;

        var logListing = BuildListing(listing);
        if (logListing.Description.Length == 0)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            if (sentHashes.Contains(logListing.HashValue)
                || inFlightHashes.Contains(logListing.HashValue)
                || !pendingHashes.Add(logListing.HashValue))
            {
                ArmFlushTimerLocked();
                return;
            }

            pendingListings.Add(logListing);
            ArmFlushTimerLocked();
        }
    }

    public Task FlushPendingAsync()
    {
        List<PartyFinderLogListing> batch;
        lock (sync)
        {
            if (disposed || IsPausedLocked())
                return Task.CompletedTask;

            batch = DrainPendingLocked();
        }

        return batch.Count == 0 ? Task.CompletedTask : SendBatchAsync(batch);
    }

    public async Task<string> TestEndpointAsync()
    {
        if (!TryGetEndpointUri(out var endpoint))
            return "Invalid logging URL.";

        var request = new PartyFinderLogBatch(
            "PFReport",
            DateTimeOffset.UtcNow,
            true,
            new[]
            {
                CreateTestListing()
            });

        try
        {
            using var message = BuildPostRequest(endpoint, request);
            using var response = await httpClient.SendAsync(message).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

            if (response.IsSuccessStatusCode)
            {
                ClearFailureState();
                return body.Length == 0 ? $"OK: {status}" : $"OK: {status} | {body}";
            }

            return body.Length == 0 ? $"Failed: {status}" : $"Failed: {status} | {body}";
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void ResetCache()
    {
        lock (sync)
        {
            pendingListings.Clear();
            pendingHashes.Clear();
            inFlightHashes.Clear();
            sentHashes.Clear();
            sentHashOrder.Clear();
            consecutiveFailures = 0;
            pausedUntilUtc = null;
            flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            pendingListings.Clear();
            pendingHashes.Clear();
            inFlightHashes.Clear();
            flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        disposeCts.Cancel();
        flushTimer.Dispose();
        httpClient.Dispose();
        disposeCts.Dispose();
    }

    private async Task SendBatchAsync(IReadOnlyCollection<PartyFinderLogListing> batch)
    {
        if (batch.Count == 0 || !configuration.LoggingEnabled || !TryGetEndpointUri(out var endpoint) || IsPaused())
            return;

        if (!await sendGate.WaitAsync(0).ConfigureAwait(false))
        {
            lock (sync)
            {
                if (!disposed)
                    RequeueBatchLocked(batch, armTimer: true);
            }

            return;
        }

        lock (sync)
        {
            if (disposed)
            {
                sendGate.Release();
                return;
            }
        }

        var hashes = batch.Select(x => x.HashValue).ToArray();
        lock (sync)
        {
            foreach (var hash in hashes)
                inFlightHashes.Add(hash);
        }

        try
        {
            var request = new PartyFinderLogBatch("PFReport", DateTimeOffset.UtcNow, false, batch);
            using var message = BuildPostRequest(endpoint, request);
            Plugin.Log.Debug("PF logging sending {Count} listings", batch.Count);
            using var response = await httpClient.SendAsync(message, disposeCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                RegisterFailure(batch, $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim());
                return;
            }

            lock (sync)
            {
                foreach (var hash in hashes)
                    AddSentHashLocked(hash);

                consecutiveFailures = 0;
                pausedUntilUtc = null;
            }

            Plugin.Log.Information("PF logging sent {Count} listings", batch.Count);
        }
        catch (OperationCanceledException) when (IsDisposed())
        {
            Plugin.Log.Debug("PF logging send canceled during dispose");
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            Plugin.Log.Debug("PF logging send stopped during dispose");
        }
        catch (Exception ex)
        {
            RegisterFailure(batch, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (sync)
            {
                foreach (var hash in hashes)
                    inFlightHashes.Remove(hash);
            }

            sendGate.Release();
        }
    }

    private HttpRequestMessage BuildPostRequest(Uri endpoint, PartyFinderLogBatch batch)
    {
        var json = JsonSerializer.Serialize(batch, JsonOptions);
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        message.Headers.UserAgent.ParseAdd("PFReport/1.0");
        if (!string.IsNullOrWhiteSpace(configuration.LoggingApiToken))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.LoggingApiToken.Trim());

        return message;
    }

    private bool IsPaused()
    {
        lock (sync)
            return IsPausedLocked();
    }

    private bool IsDisposed()
    {
        lock (sync)
            return disposed;
    }

    private bool IsPausedLocked()
    {
        if (pausedUntilUtc == null)
            return false;

        if (DateTimeOffset.UtcNow < pausedUntilUtc.Value)
            return true;

        pausedUntilUtc = null;
        consecutiveFailures = 0;
        return false;
    }

    private void ClearFailureState()
    {
        lock (sync)
        {
            consecutiveFailures = 0;
            pausedUntilUtc = null;
        }
    }

    private void RegisterFailure(IReadOnlyCollection<PartyFinderLogListing> failedBatch, string reason)
    {
        TimeSpan? retryDelay = null;
        var failureCount = 0;
        var shouldPause = false;
        lock (sync)
        {
            if (disposed)
                return;

            consecutiveFailures++;
            failureCount = consecutiveFailures;
            RequeueBatchLocked(failedBatch, armTimer: false);

            if (consecutiveFailures < FailureThreshold)
            {
                retryDelay = GetFailureRetryDelay();
                ArmFlushTimerLocked(retryDelay.Value);
            }
            else if (!IsPausedLocked())
            {
                shouldPause = true;
                pausedUntilUtc = DateTimeOffset.UtcNow.Add(FailurePauseDuration);
                pendingListings.Clear();
                pendingHashes.Clear();
                flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        if (retryDelay.HasValue)
        {
            Plugin.Log.Warning(
                "PF logging failed {FailureCount}/{FailureThreshold}: {Reason}; retrying in {RetrySeconds}s",
                failureCount,
                FailureThreshold,
                reason,
                (int)retryDelay.Value.TotalSeconds);
            return;
        }

        if (shouldPause)
        {
            Plugin.Log.Warning("PF logging paused for {PauseMinutes}m after repeated failures: {Reason}", (int)FailurePauseDuration.TotalMinutes, reason);
            Plugin.ChatGui.Print($"[PFReport] Party finder logging server looks down. Logging paused for {(int)FailurePauseDuration.TotalHours} hour.");
        }
    }

    private void RequeueBatchLocked(IReadOnlyCollection<PartyFinderLogListing> batch, bool armTimer)
    {
        if (pausedUntilUtc != null)
            return;

        foreach (var listing in batch)
        {
            if (!pendingHashes.Add(listing.HashValue))
                continue;

            pendingListings.Add(listing);
        }

        if (armTimer && pendingListings.Count > 0)
            ArmFlushTimerLocked();
    }

    private TimeSpan GetFailureRetryDelay()
    {
        var index = Math.Clamp(consecutiveFailures - 1, 0, FailureRetryDelays.Length - 1);
        return FailureRetryDelays[index];
    }

    private bool TryGetEndpointUri(out Uri endpoint)
    {
        return Uri.TryCreate(configuration.LoggingUrl, UriKind.Absolute, out endpoint!)
            && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);
    }

    private List<PartyFinderLogListing> DrainPendingLocked()
    {
        if (!disposed)
            flushTimer.Change(Timeout.Infinite, Timeout.Infinite);

        if (pendingListings.Count == 0)
            return new List<PartyFinderLogListing>();

        var batch = pendingListings.ToList();
        pendingListings.Clear();
        pendingHashes.Clear();
        return batch;
    }

    private void ArmFlushTimerLocked()
    {
        flushTimer.Change(configuration.LoggingFlushDelayMs, Timeout.Infinite);
    }

    private void ArmFlushTimerLocked(TimeSpan delay)
    {
        flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void AddSentHashLocked(ulong hash)
    {
        if (!sentHashes.Add(hash))
            return;

        sentHashOrder.Enqueue(hash);
        while (sentHashOrder.Count > configuration.LoggingSentHashCacheSize)
        {
            var oldHash = sentHashOrder.Dequeue();
            sentHashes.Remove(oldHash);
        }
    }

    private static PartyFinderLogListing BuildListing(IPartyFinderListing listing)
    {
        var name = listing.Name.ToString();
        var homeWorld = listing.HomeWorld.ValueNullable?.Name.ToString() ?? "?";
        var description = listing.Description.ToString();
        var searchArea = listing.SearchArea.ToString();
        var searchAreaRaw = (byte)listing.SearchArea;
        var dutyId = listing.RawDuty;
        var minilv = listing.MinimumItemLevel;
        var hashValue = ComputeListingHash(name, homeWorld, description, searchAreaRaw, dutyId, minilv);

        return new PartyFinderLogListing(
            hashValue,
            hashValue.ToString("x16", CultureInfo.InvariantCulture),
            listing.Id,
            name,
            homeWorld,
            description,
            searchArea,
            searchAreaRaw,
            dutyId,
            minilv,
            DateTimeOffset.UtcNow);
    }

    private static PartyFinderLogListing CreateTestListing()
    {
        const ulong hashValue = 0x5b7ef5e2d8b70391UL;
        return new PartyFinderLogListing(
            hashValue,
            hashValue.ToString("x16", CultureInfo.InvariantCulture),
            0,
            "PFReport Test",
            "Example",
            "Connectivity test only. The server must not store this row.",
            "DataCenter",
            1,
            0,
            0,
            DateTimeOffset.UtcNow);
    }

    private static ulong ComputeListingHash(string name, string homeWorld, string description, byte searchAreaRaw, ushort dutyId, ushort minilv)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        AddString(name);
        AddString(homeWorld);
        AddString(description);
        hash ^= searchAreaRaw;
        hash *= prime;
        AddUshort(dutyId);
        AddUshort(minilv);
        return hash;

        void AddString(string value)
        {
            foreach (var b in Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormKC)))
            {
                hash ^= b;
                hash *= prime;
            }

            hash ^= 0x1f;
            hash *= prime;
        }

        void AddUshort(ushort value)
        {
            hash ^= (byte)(value >> 8);
            hash *= prime;
            hash ^= (byte)value;
            hash *= prime;
        }
    }
}

internal sealed record PartyFinderLogBatch(
    string Source,
    DateTimeOffset SentAt,
    bool Test,
    IReadOnlyCollection<PartyFinderLogListing> Listings);

internal sealed record PartyFinderLogListing(
    [property: JsonIgnore]
    ulong HashValue,
    string Hash,
    ulong ListingId,
    string Name,
    string HomeWorld,
    string Description,
    string SearchArea,
    byte SearchAreaRaw,
    ushort DutyId,
    ushort Minilv,
    DateTimeOffset ObservedAt);
