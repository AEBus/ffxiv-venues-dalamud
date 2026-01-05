using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVVenues.Dalamud.Utils;

namespace FFXIVVenues.Dalamud.Commands.Brokerage;

internal sealed class CommandBroker : IDisposable
{
    private readonly ICommandManager _commandManager;
    private readonly TypeMap<ICommandHandler> _typeMap;

    public CommandBroker(IServiceProvider serviceProvider, ICommandManager commandManager)
    {
        _commandManager = commandManager;
        _typeMap = new TypeMap<ICommandHandler>(serviceProvider);
    }

    public CommandBroker ScanForCommands(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();

        var handlerTypes = assembly.GetTypes().Where(t => t.IsAssignableTo(typeof(ICommandHandler)));
        foreach (var type in handlerTypes)
        {
            var attributes = type.GetCustomAttributes<CommandAttribute>();
            foreach (var attribute in attributes)
            {
                if (attribute.CommandName is not { Length: > 0 } commandName)
                {
                    continue;
                }

                _commandManager.AddHandler(commandName, new CommandInfo(ExecuteHandler)
                {
                    HelpMessage = attribute.CommandDescription ?? string.Empty
                });
                _typeMap.Add(commandName, type);
            }
        }

        return this;
    }

    private void ExecuteHandler(string command, string args)
    {
        if (!_typeMap.ContainsKey(command))
        {
            return;
        }

        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        _typeMap.Activate(command)?.Handle(args);
    }

    public void Dispose()
    {
        foreach (var key in _typeMap.Keys)
        {
            _commandManager.RemoveHandler(key);
        }
    }
}
