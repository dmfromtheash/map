namespace Map.App.Controls;

public sealed partial class MapCanvasControl
{
    /// <summary>Импорт меняет изображения, но не документ: сбрасываем также готовый снимок слоя.</summary>
    public void RefreshHexTileLibrary()
    {
        lock (_renderResources.Gate)
        {
            // Recreated lazily on their respective rendering threads. The editor's asset source
            // and document remain alive; this does not call DisposeRenderCaches or edit history.
            _renderResources.Dispose();
        }
        QueueVisualInvalidation();
    }
}
