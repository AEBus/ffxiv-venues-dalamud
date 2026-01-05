using System;
using System.Net.Http;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVVenues.Dalamud.Commands.Brokerage;
using FFXIVVenues.Dalamud.UI;
using FFXIVVenues.Dalamud.UI.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIVVenues.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "FFXIV Venues";
    private readonly ServiceProvider _serviceProvider;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly WindowBroker _windowBroker;
    private VenueDirectoryWindow? _directoryWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this._pluginInterface = pluginInterface;
        pluginInterface.Create<PluginService>();

        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.ffxivvenues.com/v1/") };
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(pluginInterface);
        serviceCollection.AddSingleton<IUiBuilder>(_ => pluginInterface.UiBuilder);
        serviceCollection.AddSingleton(_ => pluginInterface.UiBuilder as UiBuilder ?? throw new InvalidOperationException("Dalamud returned null UiBuilder instance."));
        serviceCollection.AddSingleton(PluginService.CommandManager);
        serviceCollection.AddSingleton(PluginService.ChatGui);
        serviceCollection.AddSingleton(PluginService.TextureProvider);
        serviceCollection.AddSingleton(config);
        serviceCollection.AddSingleton(httpClient);
        serviceCollection.AddSingleton<CommandBroker>();
        serviceCollection.AddSingleton<WindowBroker>();
        serviceCollection.AddSingleton<VenueService>();

        this._serviceProvider = serviceCollection.BuildServiceProvider();
        this._windowBroker = this._serviceProvider.GetRequiredService<WindowBroker>();
        pluginInterface.UiBuilder.OpenMainUi += this.ToggleVenueDirectory;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleVenueDirectory;
        this._serviceProvider.GetService<CommandBroker>()?.ScanForCommands();
    }

    public void Dispose()
    {
        this._pluginInterface.UiBuilder.OpenMainUi -= this.ToggleVenueDirectory;
        this._pluginInterface.UiBuilder.OpenConfigUi -= this.ToggleVenueDirectory;
        this._serviceProvider.Dispose();
    }

    private void ToggleVenueDirectory()
    {
        this._directoryWindow ??= this._windowBroker.Create<VenueDirectoryWindow>();
        if (this._directoryWindow == null)
            return;

        if (this._directoryWindow.Visible)
            this._directoryWindow.Hide();
        else
            this._directoryWindow.Show();
    }
}
