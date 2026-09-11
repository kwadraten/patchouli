using Avalonia.Media;

namespace Patchouli.UI.Controls;

// Supplies decoded images for the reading view's media blocks. The asset id is the opaque
// MediaBoxPayload.AssetId recorded on the box; only the loader knows which storage it names.
// Returning null (or faulting) means "no preview available": the reading view keeps the
// block's placeholder card and does not retry for that asset id while the loader stays set.
public interface IMediaImageLoader
{
    Task<IImage?> LoadAsync(string assetId, CancellationToken cancellationToken);
}
