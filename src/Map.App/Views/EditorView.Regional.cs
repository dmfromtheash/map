using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Assets;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control? _regionalLibraryEmpty;
    private Control BuildRegionalModes()
    {
        var choices = new StackPanel { Spacing = 4 };
        foreach (var (mode, name, hint) in new[] {
            (RegionalPlacementMode.Forest,"Лес по области","Задайте границу. Густоту, размеры и опушку можно менять позднее."),
            (RegionalPlacementMode.Ridge,"Хребет","Вершины — по редактируемой линии; склоны — область у уже заданных обрывов."),
            (RegionalPlacementMode.Settlement,"Поселение по области","Группы домов со свободными проходами. Каждый дом можно отделить от группы.") })
        {
            var button = Ui.TextButton("Regional" + mode + "Button", name, () =>
            { _editor.StartRegionalPlacement(mode); SyncSidePanels(); }, "compact");
            ToolTip.SetTip(button,hint); choices.Children.Add(button);
        }
        var section = new Ui.SectionPanel("RegionalModes", "Готовые сцены", new StackPanel { Spacing = 4, Children =
        {
            Ui.Hint("Строят редактируемую область или путь из ваших наборов с назначенной ролью."),
            choices,
        } }, false);
        section.SetSummary("лес, хребет, поселение");
        section.IsVisible = _editor.IsLibraryAvailable;
        return section;
    }

    private Control BuildAppearanceTransfer()
    {
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("muted","role-caption");
        Button apply = null!, cancel = null!;
        LocalBrushPreset[] previous=[];
        var name = new TextBox { Name="RegionalPresetName", PlaceholderText="Название пресета", MaxLength=80 };
        var presets = new ComboBox { Name="RegionalPresetChoice", HorizontalAlignment=HorizontalAlignment.Stretch,
            ItemTemplate=new FuncDataTemplate<LocalBrushPreset>((p,_)=>new TextBlock { Text=p is null?"":p.Name,
                TextTrimming=TextTrimming.CharacterEllipsis }) };
        var copy = Ui.TextButton("CopyAppearanceButton","Скопировать вид",()=>{_editor.CopyAppearance(); Sync();},"compact");
        var preview = Ui.TextButton("PreviewAppearanceButton","Предпросмотр",()=>{_editor.PreviewAppearanceTransfer(); Sync();},"compact");
        apply = Ui.TextButton("ApplyAppearanceButton","Применить",()=>{_editor.ApplyAppearanceTransfer(); Sync();},"compact");
        cancel = Ui.TextButton("CancelAppearanceButton","Отменить просмотр",()=>{_editor.CancelAppearanceTransfer(); Sync();},"compact");
        var load=Ui.TextButton("LoadRegionalPresetButton","Загрузить пресет",()=>
        { if(presets.SelectedItem is LocalBrushPreset p) _editor.ApplyRegionalPreset(p.Id); SyncSidePanels(); Sync(); },"compact");
        var savePlacement=Ui.TextButton("SavePlacementPresetButton","Сохранить размещение",()=>
        { _editor.SavePlacementPreset(name.Text??""); Sync(); },"compact");
        var saveAppearance=Ui.TextButton("SaveAppearancePresetButton","Сохранить скопированный вид",()=>
        { _editor.SaveAppearancePreset(name.Text??""); Sync(); },"compact");
        var rename=Ui.TextButton("RenameRegionalPresetButton","Переименовать",()=>
        { if(presets.SelectedItem is LocalBrushPreset p) _editor.RenameBrushPreset(p.Id,name.Text??""); Sync(); },"compact");
        var remove=Ui.TextButton("DeleteRegionalPresetButton","Удалить пресет",()=>
        { if(presets.SelectedItem is LocalBrushPreset p) _editor.DeleteBrushPreset(p.Id); Sync(); },"compact");
        void Sync()
        {
            summary.Text=_editor.AppearanceTransferSummary; apply.IsEnabled=_editor.HasAppearanceTransferPreview;
            cancel.IsEnabled=_editor.HasAppearanceTransferPreview;
            var values=_editor.RegionalPresets.ToArray();
            if(!previous.SequenceEqual(values))
            {
                var id=(presets.SelectedItem as LocalBrushPreset)?.Id; previous=values; presets.ItemsSource=values;
                presets.SelectedItem=values.FirstOrDefault(p=>p.Id==id)??values.LastOrDefault();
            }
        }
        _appearanceSyncs.Add(Sync); Sync();
        var content = new StackPanel { Spacing=6, Children={
            summary,new Ui.ChoiceGrid { MaxColumns=2,MinimumItemWidth=100,Gap=4,Children={copy,preview,apply,cancel} },
            Disclosure("RegionalPresetsPanel","Мои пресеты",new StackPanel { Spacing=5,Children={
                presets,load,name,savePlacement,saveAppearance,
                new Ui.ChoiceGrid { MaxColumns=2,MinimumItemWidth=100,Gap=4,Children={rename,remove} } } },false) } };
        return Disclosure("AppearanceTransferPanel","Перенос вида и пресеты",new ScrollViewer { MaxHeight=200,
            HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,Content=content },false);
    }

    private Control RegionalPackButton(string name)
    {
        string title; try { title = RegionalArtPack.IsAvailable ? RegionalArtPack.Load().Name : "Тихий атлас"; } catch { title = "Тихий атлас"; }
        var button = Ui.AsyncButton(name,$"Подключить «{title}»",async()=>
        { await _editor.ImportRegionalPackAsync(); SyncAssetList(); },"compact");
        button.IsVisible = _editor.CanImportRegionalPack;
        return button;
    }

    private Control BuildRegionalLibraryEmpty()
    {
        _regionalLibraryEmpty=new Border { Padding=new Thickness(24),HorizontalAlignment=HorizontalAlignment.Center,
            VerticalAlignment=VerticalAlignment.Center,MaxWidth=440, Child=new StackPanel { Spacing=12, Children={
                Ui.EmptyState("LibraryEmptyState",MapIcons.ObjectStamp,"Здесь появятся ваши изображения",
                    _editor.CanImportRegionalPack
                        ? "Импортируйте свои PNG, JPEG или WebP либо подключите оригинальный комплект региона. Он добавится к вашей библиотеке."
                        : "Импортируйте свои PNG, JPEG или WebP — они добавятся к вашей библиотеке."),
                RegionalPackButton("EmptyLibraryRegionalPackButton") } } };
        return _regionalLibraryEmpty;
    }
}
