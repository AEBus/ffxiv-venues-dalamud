using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace FFXIVVenues.Dalamud.UI.Abstractions;

internal abstract class Window
{
    private bool _visible;
    private bool _sizeInitialized;

    protected Window(UiBuilder uiBuilder)
    {
        uiBuilder.Draw += Draw;
    }

    public bool Visible => _visible;

    protected string Title { get; set; } = "FFXIV Venues";
    protected Vector2 InitialSize { get; set; } = new(600, 450);
    protected Vector2 MinimumSize { get; set; } = new(300, 200);
    protected Vector2 MaximumSize { get; set; } = new(1000, 1000);
    protected ImGuiWindowFlags WindowFlags { get; set; } = ImGuiWindowFlags.None;

    public void Show() => _visible = true;

    public void Hide() => _visible = false;

    public void Draw()
    {
        if (!_visible)
        {
            return;
        }

        if (!_sizeInitialized)
        {
            ImGui.SetNextWindowSize(InitialSize, ImGuiCond.FirstUseEver);
            _sizeInitialized = true;
        }

        ImGui.SetNextWindowSizeConstraints(MinimumSize, MaximumSize);
        if (ImGui.Begin(Title, ref _visible, WindowFlags))
        {
            Render();
        }

        ImGui.End();
    }

    public abstract void Render();
}
