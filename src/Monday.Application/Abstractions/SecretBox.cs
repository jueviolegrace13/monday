namespace Monday.Application.Abstractions;

/// <summary>
/// Seals secrets Monday must use later, such as a project's model API key, under a master key that
/// never sits in the database. A database leak alone reveals nothing.
/// </summary>
public interface ISecretBox
{
    /// <summary>False when no master key is configured; then model settings and chat are off.</summary>
    bool Enabled { get; }

    string Seal(string plain);

    /// <summary>Opens a value from <see cref="Seal"/>. Throws if it was changed or sealed under another master key.</summary>
    string Open(string sealedValue);
}
