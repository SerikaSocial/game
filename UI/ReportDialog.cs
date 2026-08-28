using System;
using Godot;
using Serika.Net;

namespace SerikaSocial.UI;

/// Report-abuse dialog, shared by player and world targets. Category picker + optional
/// details line, filed via the API. The details field is a single-line LineEdit on
/// purpose: VrKeyboard only attaches to LineEdit, and a report a VR player cannot type
/// is a report they cannot file.
///
/// Follows the house modal pattern: CanvasLayer + Brand.Scrim + centered card, Open/Hide,
/// Esc closes, sizes through Brand.Card/Fs so it fits the VR panel.
public partial class ReportDialog : CanvasLayer
{
    /// Fired after a report is successfully filed (Main shows a toast).
    public event Action Filed;

    private ApiClient _api;
    private ColorRect _scrim;
    private PanelContainer _card;
    private Label _title;
    private OptionButton _category;
    private LineEdit _details;
    private Label _status;
    private Button _submit;
    private VBoxContainer _formBox;
    private VBoxContainer _doneBox;

    private bool _world;
    private string _targetId;
    private string _targetName;

    /// Categories 0..8 — must match REPORT_CATEGORIES in server/api/src/routes/reports.ts.
    private static readonly string[] Categories =
    {
        "Harassment or bullying",
        "Hate speech",
        "Sexual content",
        "Violence or gore",
        "Spam or scam",
        "Impersonation",
        "Cheating or exploiting",
        "Inappropriate content",
        "Something else",
    };

    public ReportDialog()
    {
        Layer = 110; // above every other menu — reports can be filed from anywhere
    }

    public override void _Ready()
    {
        Visible = false;

        _scrim = Brand.Scrim(0.82f);
        AddChild(_scrim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _card = new PanelContainer();
        _card.AddThemeStyleboxOverride("panel", Brand.Panel(Brand.Bg1, 16));
        _card.CustomMinimumSize = Brand.Card(520, 420);
        center.AddChild(_card);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 20);
        _card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        _title = new Label { Text = "Report" };
        _title.AddThemeFontSizeOverride("font_size", Brand.Fs(18));
        _title.AddThemeColorOverride("font_color", Brand.TextHi);
        vbox.AddChild(_title);

        var intro = new Label
        {
            Text = "Goes to the Serika moderation team. They are not told who reported them.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        intro.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        intro.AddThemeColorOverride("font_color", Brand.TextDim);
        vbox.AddChild(intro);

        _formBox = new VBoxContainer();
        _formBox.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(_formBox);

        var catLabel = new Label { Text = "What happened?" };
        catLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        catLabel.AddThemeColorOverride("font_color", Brand.TextMid);
        _formBox.AddChild(catLabel);

        _category = new OptionButton();
        foreach (var c in Categories) _category.AddItem(c);
        _category.Selected = Categories.Length - 1;
        _category.CustomMinimumSize = new Vector2(0, 40 * (Brand.Fs(14) / 14f));
        _formBox.AddChild(_category);

        var detLabel = new Label { Text = "Details (optional)" };
        detLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(13));
        detLabel.AddThemeColorOverride("font_color", Brand.TextMid);
        _formBox.AddChild(detLabel);

        _details = new LineEdit
        {
            PlaceholderText = "What happened? Where were you?",
            CustomMinimumSize = new Vector2(0, 40 * (Brand.Fs(14) / 14f)),
        };
        _formBox.AddChild(_details);

        _status = new Label { Text = "" };
        _status.AddThemeFontSizeOverride("font_size", Brand.Fs(12));
        _status.AddThemeColorOverride("font_color", Brand.Warning);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _formBox.AddChild(_status);

        _doneBox = new VBoxContainer();
        _doneBox.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(_doneBox);
        var doneLabel = new Label
        {
            Text = "Report filed. Thank you — moderators will review it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        doneLabel.AddThemeFontSizeOverride("font_size", Brand.Fs(14));
        doneLabel.AddThemeColorOverride("font_color", Brand.Success);
        _doneBox.AddChild(doneLabel);
        var doneBtn = Brand.Primary_(new Button { Text = "Done", CustomMinimumSize = new Vector2(120, 42) });
        doneBtn.Pressed += Hide;
        var doneWrap = new CenterContainer();
        doneWrap.AddChild(doneBtn);
        _doneBox.AddChild(doneWrap);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 10);
        buttonRow.Alignment = BoxContainer.AlignmentMode.End;
        _formBox.AddChild(buttonRow);

        var cancel = Brand.Ghost_(new Button { Text = "Cancel", CustomMinimumSize = new Vector2(110, 42) });
        cancel.Pressed += Hide;
        buttonRow.AddChild(cancel);

        _submit = Brand.Primary_(new Button { Text = "Submit report", CustomMinimumSize = new Vector2(150, 42) });
        _submit.Pressed += SubmitPressed;
        buttonRow.AddChild(_submit);
    }

    public void Configure(ApiClient api) => _api = api;

    public void OpenForUser(string userId, string displayName)
    {
        _world = false;
        _targetId = userId;
        _targetName = displayName;
        _title.Text = $"Report player: {displayName}";
        Begin();
    }

    public void OpenForWorld(string worldId, string worldName)
    {
        _world = true;
        _targetId = worldId;
        _targetName = worldName;
        _title.Text = $"Report world: {worldName}";
        Begin();
    }

    private void Begin()
    {
        _details.Text = "";
        _category.Selected = Categories.Length - 1;
        _status.Text = "";
        _submit.Disabled = false;
        _formBox.Visible = true;
        _doneBox.Visible = false;
        _scrim.Visible = _card.Visible = Visible = true;
    }

    public new void Hide()
    {
        _scrim.Visible = false;
        _card.Visible = false;
        Visible = false;
        _details.ReleaseFocus();
    }

    public bool IsOpen => Visible;

    private async void SubmitPressed()
    {
        if (_api == null || string.IsNullOrEmpty(_targetId)) return;
        _submit.Disabled = true;
        _status.Text = "Filing report…";
        _status.AddThemeColorOverride("font_color", Brand.TextDim);
        try
        {
            string error = await _api.ReportAsync(_world, _targetId, _category.Selected, _details.Text?.Trim() ?? "");
            if (error == null)
            {
                _formBox.Visible = false;
                _doneBox.Visible = true;
                Filed?.Invoke();
                return;
            }
            _status.Text = error switch
            {
                "already_reported" => "You already have an open report about this. Moderators are on it.",
                "report_rate_limited" => "You've filed a lot of reports today — please try again tomorrow.",
                _ => $"Could not file report ({error})",
            };
            _status.AddThemeColorOverride("font_color", Brand.Warning);
        }
        catch (Exception e)
        {
            GD.PrintErr($"report submit failed: {e.Message}");
            _status.Text = "Could not file report — check your connection.";
            _status.AddThemeColorOverride("font_color", Brand.Warning);
        }
        finally
        {
            _submit.Disabled = false;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && IsOpen)
        {
            Hide();
            GetViewport().SetInputAsHandled();
        }
    }
}
