namespace HisabTak.Application;

/// <summary>Technical identity, independent of customer and company settings.</summary>
public sealed record ApplicationIdentity(string Name, string Version);
