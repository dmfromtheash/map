using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildPassportTextureKindChoice()
    {
        var choice = AppearanceChoice("PassportTextureKindBox", "Текстура",
            [(nameof(PassportTextureKind.None), "нет"),
             (nameof(PassportTextureKind.Ground), "Земля"),
             (nameof(PassportTextureKind.Water), "Вода"),
             (nameof(PassportTextureKind.Floor), "Пол"),
             (nameof(PassportTextureKind.Paint), "Краска для кисти")],
            () => _editor.SelectedSetTextureKind.ToString(),
            key =>
            {
                _editor.SetSelectedSetTextureKind(Enum.Parse<PassportTextureKind>(key));
                SyncAssetList();
                SyncAppearanceSections();
                SyncStatus();
            });
        _appearanceSyncs.Add(() => choice.IsEnabled = _editor.SelectedSet is not null);
        return choice;
    }

    private TextBlock BuildPassportDetailText()
    {
        var detail = new TextBlock
        {
            Name = "SelectedSetDetailText",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
        }.Classed("role-caption");
        _appearanceSyncs.Add(() =>
        {
            detail.Text = _editor.SelectedSet?.Detail ?? "";
            detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text);
            AutomationProperties.SetName(detail, detail.Text);
        });
        return detail;
    }
}
