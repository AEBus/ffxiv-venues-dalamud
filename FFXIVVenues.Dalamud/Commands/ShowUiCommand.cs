using System.Threading.Tasks;
using FFXIVVenues.Dalamud.Commands.Brokerage;
using FFXIVVenues.Dalamud.UI;
using FFXIVVenues.Dalamud.UI.Abstractions;

namespace FFXIVVenues.Dalamud.Commands;

[Command("/venues", "Show all venues")]
internal class ShowUiCommand : ICommandHandler
{
    private readonly WindowBroker _windowBroker;

    public ShowUiCommand(WindowBroker windowBroker)
    {
        _windowBroker = windowBroker;
    }

    public Task Handle(string args)
    {
        _windowBroker.Create<VenueDirectoryWindow>()?.Show();
        return Task.CompletedTask;
    }
}
