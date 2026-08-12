using UnityEngine;

/// <summary>
/// Совместимость со старым именем. Слушатель UDP — <see cref="StreamCommandReceiver"/>.
/// </summary>
[System.Obsolete("Use StreamCommandReceiver")]
public sealed class StreamBotUdpReceiver : StreamCommandReceiver
{
    // Bootstrap отключён — слушает StreamCommandReceiver.
}
