using System;

namespace FFXIVVenues.Dalamud.Commands.Brokerage;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class CommandAttribute : Attribute
{
    public string CommandName { get; }
    public string? CommandDescription { get; }

    public CommandAttribute(string commandName, string? commandDescription = null)
    {
        CommandName = commandName ?? throw new ArgumentNullException(nameof(commandName));
        CommandDescription = commandDescription;
    }
}
