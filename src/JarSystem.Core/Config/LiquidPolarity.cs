using System.Text.Json.Serialization;

namespace JarSystem.Core.Config;

/// <summary>
/// Structural role of one of a World's two mutually exclusive liquid identities.
/// This is not a resource name or gameplay label.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LiquidPolarity
{
    Cathode,
    Anode
}
