using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

internal sealed class SettingsManager
{
    private readonly Settings _settings;

    /// <summary>Raised whenever the user edits a setting in the Command Palette settings UI.</summary>
    public event EventHandler? SettingsUpdated;

    public SettingsManager()
    {
        _settings = new Settings();

        var refreshMinutes = new ChoiceSetSetting(
            "refreshMinutes",
            "Refresh rate",
            "How often journeys are refreshed (minutes)",
            [
                new ChoiceSetSetting.Choice("5 minutes", "5"),
                new ChoiceSetSetting.Choice("1 minute", "1"),
                new ChoiceSetSetting.Choice("15 minutes", "15"),
                new ChoiceSetSetting.Choice("30 minutes", "30"),
                new ChoiceSetSetting.Choice("60 minutes", "60"),
            ]);

        var openOnActivate = new ToggleSetting(
            "openOnActivate",
            "Open itinerary on click",
            "When enabled, clicking a route in the Dock opens the transit itinerary in your browser. When disabled, clicking refreshes the journey data.",
            true);

        _settings.Add(refreshMinutes);
        _settings.Add(openOnActivate);

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public ICommandSettings Settings => _settings;

    public ICommand SettingsPage => _settings.SettingsPage;

    public int RefreshMinutes
    {
        get
        {
            var raw = _settings.GetSetting<string>("refreshMinutes");
            return int.TryParse(raw, out var minutes) && minutes > 0 ? minutes : 5;
        }
    }

    public bool OpenOnActivate => _settings.GetSetting<bool>("openOnActivate");

    private void OnSettingsChanged(object sender, Settings args)
    {
        SettingsUpdated?.Invoke(this, EventArgs.Empty);
    }
}
