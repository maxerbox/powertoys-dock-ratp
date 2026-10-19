using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

/// <summary>
/// Adaptive-card form to add or edit a journey. Uses the official Command
/// Palette form pattern (<c>ContentPage</c> + <c>FormContent</c>): three text
/// inputs (name, from, to) and a submit button that persists to the
/// <see cref="RoutesStore"/>.
/// </summary>
internal sealed partial class RouteFormPage : ContentPage
{
    private readonly RouteFormContent _form;

    public RouteFormPage(RoutesStore store, SavedRoute? existing = null)
    {
        _form = new RouteFormContent(store, existing);
        Name = "Open";
        Title = existing is null ? "Add journey" : $"Edit {existing.Name}";
        Icon = new IconInfo(existing is null ? "\uE710" : "\uE70F"); // Add / Edit
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class RouteFormContent : FormContent
{
    private readonly RoutesStore _store;
    private readonly SavedRoute? _existing;

    public RouteFormContent(RoutesStore store, SavedRoute? existing)
    {
        _store = store;
        _existing = existing;

        TemplateJson = """
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [
                {
                    "type": "Input.Text",
                    "id": "Name",
                    "label": "Journey name",
                    "placeholder": "e.g. Home to Work",
                    "isRequired": true,
                    "errorMessage": "A name is required",
                    "value": "${name}"
                },
                {
                    "type": "Input.Text",
                    "id": "From",
                    "label": "From",
                    "placeholder": "e.g. 10 Rue de Rivoli, Paris — or 48.8566,2.3522",
                    "isRequired": true,
                    "errorMessage": "A start address is required",
                    "value": "${from}"
                },
                {
                    "type": "Input.Text",
                    "id": "To",
                    "label": "To",
                    "placeholder": "e.g. Château de Versailles, Versailles — or 48.8049,2.1201",
                    "isRequired": true,
                    "errorMessage": "A destination is required",
                    "value": "${to}"
                },
                {
                    "type": "TextBlock",
                    "text": "Addresses are geocoded by the Transitous journey planner (same Île-de-France Mobilités data as Bonjour RATP). You can also enter raw lat,lng coordinates.",
                    "isVisible": true,
                    "wrap": true,
                    "size": "Small",
                    "spacing": "Medium"
                }
            ],
            "actions": [
                {
                    "type": "Action.Submit",
                    "title": "${submitTitle}"
                }
            ]
        }
        """;

        DataJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = existing?.Name ?? string.Empty,
            ["from"] = existing?.From ?? string.Empty,
            ["to"] = existing?.To ?? string.Empty,
            ["submitTitle"] = existing is null ? "Add journey" : "Save changes",
        });
    }

    public override CommandResult SubmitForm(string payload)
    {
        var input = JsonNode.Parse(payload)?.AsObject();
        if (input is null)
        {
            return CommandResult.GoHome();
        }

        var name = input["Name"]?.ToString()?.Trim() ?? string.Empty;
        var from = input["From"]?.ToString()?.Trim() ?? string.Empty;
        var to = input["To"]?.ToString()?.Trim() ?? string.Empty;

        if (name.Length == 0 || from.Length == 0 || to.Length == 0)
        {
            return CommandResult.ShowToast("Name, from and to are all required");
        }

        var route = new SavedRoute
        {
            // Keep the id when editing so the journey cache and dock band follow along.
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            From = from,
            To = to,
        };

        _store.Upsert(route);
        new ToastStatusMessage($"{(_existing is null ? "Added" : "Saved")} journey '{name}'").Show();
        return CommandResult.GoHome();
    }
}
