namespace SoulBuddy.Services;

public sealed class LocationMapper
{
    private readonly Dictionary<int, string> _locations = new()
    {
        [126] = "Starter",
        [178] = "Route 30",
        [179] = "Route 31",
        [180] = "Route 32",
        [204] = "Placeholder 1",
        [220] = "Finsterhöhle"
    };

    public string? GetLocationName(int locationId)
    {
        return _locations.GetValueOrDefault(locationId);
    }
}