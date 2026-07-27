namespace SoulSync.Services;

public sealed class LocationMapper
{
    private readonly Dictionary<int, string> _locations = new()
    {
        // Diese Werte müssen wir noch exakt überprüfen.
        // Trage zunächst nur bestätigte Werte ein.
        [220] = "Finsterhöhle",
        [179] = "Route 31",
        [178] = "Route 30",

        // Beispiel:
        // [177] = "Route 29"
    };

    public string? GetLocationName(int locationId)
    {
        return _locations.GetValueOrDefault(locationId);
    }
}