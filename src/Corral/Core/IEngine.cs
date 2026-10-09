using System.Diagnostics;
using Corral.Models;

namespace Corral.Core;

/// <summary>
/// Ce que l'interface attend du moteur : le moteur local (<see cref="Engine"/>, exe portable)
/// ou le service Windows joint par pipe (<see cref="Corral.Service.RemoteEngine"/>, installation MSI).
/// </summary>
public interface IEngine
{
    event Action<EngineSnapshot>? SnapshotReady;
    event Action<IReadOnlyList<string>, double>? ProBalanceActed;
    event Action<bool, string?>? GameModeChanged;
    event Action<string, string>? Notification;

    string? KeepAwakeReason { get; }
    bool GameModeManual { get; }

    void Start();
    void Stop();
    void UpdateSettings(Settings settings);
    void SetPaused(bool value);
    void SetGameMode(bool enabled);
    /// <summary>Change la priorité une fois ; renvoie un message d'erreur ou null.</summary>
    string? SetPriorityOnce(int pid, string name, ProcessPriorityClass priority);
    void CleanMemoryNow();
}

/// <summary>Où l'interface enregistre la configuration : fichier local, ou service.</summary>
public interface ISettingsStore
{
    /// <summary>Dossier des fichiers propres à l'utilisateur (statistiques, journal de l'interface).</summary>
    string ConfigDirectory { get; }
    void Save(Settings settings);
}
