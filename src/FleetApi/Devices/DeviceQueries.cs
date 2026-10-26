using System.Globalization;
using FleetPulse.Contracts.Ids;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace FleetPulse.FleetApi.Devices;

public sealed class DeviceQueries(FleetDbContext db, IConnectionMultiplexer redis)
{
    private static readonly RedisValue[] SnapshotFields =
    [
        "online", "health", "lastSeenAt", "cpuPercent", "memoryPercent", "temperatureCelsius"
    ];

    public async Task<FleetSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var devices = await db.Devices.AsNoTracking()
            .Select(device => new { device.Id, device.Site.RegionId, RegionName = device.Site.Region.Name })
            .ToListAsync(cancellationToken);
        var snapshots = await GetSnapshotsAsync(devices.Select(device => device.Id), cancellationToken);
        var counts = await redis.GetDatabase().HashGetAsync(RedisFleetState.CountsKey,
            ["online", "healthy", "degraded", "unhealthy"]);
        var regions = devices.Zip(snapshots)
            .GroupBy(pair => new { pair.First.RegionId, pair.First.RegionName })
            .OrderBy(group => group.Key.RegionName)
            .Select(group => new RegionSummary(
                group.Key.RegionId,
                group.Key.RegionName,
                group.Count(),
                group.Count(pair => pair.Second.Online),
                group.Count(pair => !pair.Second.Online),
                group.Count(pair => pair.Second.Online && pair.Second.Health == "healthy"),
                group.Count(pair => pair.Second.Online && pair.Second.Health == "degraded"),
                group.Count(pair => pair.Second.Online && pair.Second.Health == "unhealthy")))
            .ToArray();
        var online = ParseInt(counts[0]);
        return new FleetSummary(devices.Count, online, Math.Max(0, devices.Count - online),
            ParseInt(counts[1]), ParseInt(counts[2]), ParseInt(counts[3]), regions);
    }

    public async Task<DevicePage> SearchAsync(DeviceSearch search, CancellationToken cancellationToken)
    {
        var query = db.Devices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Query))
        {
            query = query.Where(device => device.SerialNumber.Contains(search.Query) ||
                device.Model.Contains(search.Query));
        }

        if (search.RegionId is int regionId)
        {
            query = query.Where(device => device.Site.RegionId == regionId);
        }

        if (search.SiteId is int siteId)
        {
            query = query.Where(device => device.SiteId == siteId);
        }

        var offset = (int)Math.Min((long)(search.Page - 1) * search.PageSize, int.MaxValue);
        if (search.Online is null && search.Health is null)
        {
            var total = await query.CountAsync(cancellationToken);
            var pageRows = await Project(query.OrderBy(device => device.SerialNumber)
                .Skip(offset).Take(search.PageSize)).ToListAsync(cancellationToken);
            var pageSnapshots = await GetSnapshotsAsync(pageRows.Select(device => device.Id), cancellationToken);
            return new DevicePage(total, search.Page, search.PageSize,
                pageRows.Zip(pageSnapshots).Select(pair => ToListItem(pair.First, pair.Second)).ToArray());
        }

        var devices = await Project(query.OrderBy(device => device.SerialNumber))
            .ToListAsync(cancellationToken);
        var snapshots = await GetSnapshotsAsync(devices.Select(device => device.Id), cancellationToken);
        var matches = devices.Zip(snapshots)
            .Where(pair => (search.Online is null || pair.Second.Online == search.Online) &&
                (search.Health is null || pair.Second.Health == search.Health))
            .ToArray();
        var page = matches.Skip(offset).Take(search.PageSize)
            .Select(pair => ToListItem(pair.First, pair.Second))
            .ToArray();
        return new DevicePage(matches.Length, search.Page, search.PageSize, page);
    }

    private static IQueryable<DeviceRow> Project(IQueryable<Device> query) =>
        query
            .Select(device => new DeviceRow(device.Id, device.SerialNumber, device.Model,
                device.FirmwareVersion, device.SiteId, device.Site.Name,
                device.Site.RegionId, device.Site.Region.Name));

    public async Task<DeviceDetail?> GetDetailAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking()
            .Where(item => item.Id == deviceId)
            .Select(item => new DeviceRow(item.Id, item.SerialNumber, item.Model,
                item.FirmwareVersion, item.SiteId, item.Site.Name,
                item.Site.RegionId, item.Site.Region.Name))
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(deviceId, cancellationToken);
        return new DeviceDetail(ToListItem(device, snapshot), snapshot.CpuPercent,
            snapshot.MemoryPercent, snapshot.TemperatureCelsius);
    }

    private async Task<DeviceSnapshot[]> GetSnapshotsAsync(IEnumerable<DeviceId> deviceIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var tasks = deviceIds.Select(id => database.HashGetAsync(RedisFleetState.DeviceKey(id), SnapshotFields));
        var values = await Task.WhenAll(tasks);
        return values.Select(ToSnapshot).ToArray();
    }

    private async Task<DeviceSnapshot> GetSnapshotAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await redis.GetDatabase().HashGetAsync(RedisFleetState.DeviceKey(deviceId), SnapshotFields);
        return ToSnapshot(values);
    }

    private static DeviceSnapshot ToSnapshot(RedisValue[] values)
    {
        var lastSeen = long.TryParse(values[2].ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : (DateTimeOffset?)null;
        return new DeviceSnapshot(values[0] == "1", values[1].IsNull ? null : values[1].ToString(),
            lastSeen, ParseDouble(values[3]), ParseDouble(values[4]), ParseDouble(values[5]));
    }

    private static DeviceListItem ToListItem(DeviceRow device, DeviceSnapshot snapshot) =>
        new(device.Id, device.SerialNumber, device.Model, device.FirmwareVersion,
            device.SiteId, device.SiteName, device.RegionId, device.RegionName,
            snapshot.Online, snapshot.Health, snapshot.LastSeenAt);

    private static int ParseInt(RedisValue value) =>
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : 0;

    private static double? ParseDouble(RedisValue value) =>
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number : null;

    private sealed record DeviceRow(DeviceId Id, string SerialNumber, string Model,
        string FirmwareVersion, int SiteId, string SiteName, int RegionId, string RegionName);

    private sealed record DeviceSnapshot(bool Online, string? Health, DateTimeOffset? LastSeenAt,
        double? CpuPercent, double? MemoryPercent, double? TemperatureCelsius);
}

public sealed record FleetSummary(int TotalDevices, int OnlineDevices, int OfflineDevices,
    int HealthyDevices, int DegradedDevices, int UnhealthyDevices, IReadOnlyList<RegionSummary> Regions);

public sealed record RegionSummary(int RegionId, string RegionName, int TotalDevices,
    int OnlineDevices, int OfflineDevices, int HealthyDevices, int DegradedDevices, int UnhealthyDevices);

public sealed record DeviceSearch(string? Query, int? RegionId, int? SiteId, bool? Online,
    string? Health, int Page, int PageSize);

public sealed record DevicePage(int Total, int Page, int PageSize, IReadOnlyList<DeviceListItem> Items);

public sealed record DeviceListItem(DeviceId Id, string SerialNumber, string Model,
    string FirmwareVersion, int SiteId, string SiteName, int RegionId, string RegionName,
    bool Online, string? Health, DateTimeOffset? LastSeenAt);

public sealed record DeviceDetail(DeviceListItem Device, double? CpuPercent,
    double? MemoryPercent, double? TemperatureCelsius);
