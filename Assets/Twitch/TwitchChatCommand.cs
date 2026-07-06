using System;

/// <summary>Команда чата вида #add_tree=5</summary>
[Serializable]
public readonly struct TwitchChatCommand
{
    public readonly string Username;
    public readonly string DisplayName;
    public readonly string RawMessage;
    public readonly string CommandName;
    public readonly int IntValue;

    public TwitchChatCommand(string username, string displayName, string rawMessage, string commandName, int intValue)
    {
        Username = username;
        DisplayName = displayName;
        RawMessage = rawMessage;
        CommandName = commandName;
        IntValue = intValue;
    }

    public override string ToString() => $"[{DisplayName}] #{CommandName}={IntValue}";
}
