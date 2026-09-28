namespace RouterBalancing.Core.Engine;

/// <summary>Mức ưu tiên request trong queue — luật 1-Highest xem <see cref="RequestQueue"/>.</summary>
public enum RequestPriority
{
    Normal = 0,
    High = 1,
    Highest = 2,
}
