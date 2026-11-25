using System;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using Elements.Core;
using System.Text;
using FrooxEngine.Store;
using Elements.Assets;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Point = SixLabors.ImageSharp.Point;
using System.Net.Http;
using Renderite.Shared;

namespace GifImporter;

public class GifImporter : ResoniteMod
{
    public override string Name => "GifImporter";
    public override string Author => "astral\n<i><size=50%>LeCloutpanda(Maintainer)";
    public override string Version => "1.2.3";
    public override string Link => "https://github.com/lecloutpanda/GifImporter";

    [AutoRegisterConfigKey]
    public static ModConfigurationKey<bool> KEY_SQUARE = new ModConfigurationKey<bool>(
        "Square spritesheet",
        "Generate square spritesheet",
        () => true);

    public static ModConfiguration? config;

    public override void OnEngineInit()
    {
        Harmony harmony = new Harmony("xyz.astralchan.gifimporter");
        harmony.PatchAll();
        config = GetConfiguration();
    }

    [HarmonyPatch(typeof(ImageImporter), "ImportImage")]
    class GifImporterPatch
    {
        public static bool Prefix(ref Task<Result> __result, ImportItem item, Slot targetSlot, bool addCollider,
            ImageProjection projection, StereoLayout stereoLayout, float3? forward, TextureConversion convert,
            bool setupScreenshotMetadata, bool pointFiltering, bool uncompressed, bool alphaBleed, bool stripMetadata)
        {
            Uri? uri = item.assetUri ?? (Uri.TryCreate(item.filePath, UriKind.Absolute, out var u) ? u : null);
            if (uri == null)
            {
                return true; // Not a valid URI, fallback to default importer
            }

            bool isGif = false;
            if (uri.Scheme == "file" && item.filePath != null)
            {
                // Check magic number for GIF
                using var fs = new FileStream(item.filePath, FileMode.Open, FileAccess.Read);
                byte[] header = new byte[6];
                if (fs.Read(header, 0, 6) == 6 &&
                    (Encoding.ASCII.GetString(header) == "GIF87a" || Encoding.ASCII.GetString(header) == "GIF89a"))
                {
                    isGif = true;
                }
            }
            else if (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "resdb")
            {
                isGif = true;
            }

            if (!isGif)
                return true; // Not a GIF, fallback

            __result = targetSlot.StartTask<Result>(async delegate ()
            {
                await default(ToBackground);

                Image<Rgba32> gif;
                LocalDB localDB = targetSlot.World.Engine.LocalDB;

                try
                {
                    if (uri.Scheme == "file")
                    {
                        gif = Image.Load<Rgba32>(item.filePath);
                    }
                    else if (uri.Scheme == "http" || uri.Scheme == "https")
                    {
                        using var client = new HttpClient();
                        using var stream = await client.GetStreamAsync(uri);
                        gif = Image.Load<Rgba32>(stream);
                    }
                    else if (uri.Scheme == "resdb")
                    {
                        using var stream = await localDB.TryOpenAsset(uri);
                        gif = Image.Load<Rgba32>(stream);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported URI scheme");
                    }

                    int frameCount = gif.Frames.Count;
                    int frameWidth = gif.Width;
                    int frameHeight = gif.Height;

                    // Calculate sheet size
                    int cols = config?.GetValue(KEY_SQUARE) == true
                        ? (int)Math.Ceiling(Math.Sqrt(frameCount))
                        : frameCount;
                    int rows = (int)Math.Ceiling(frameCount / (float)cols);

                    using var spriteSheet = new Image<Rgba32>(frameWidth * cols, frameHeight * rows);

                    // Copy frames into spritesheet
                    for (int i = 0; i < frameCount; i++)
                    {
                        int row = i / cols;
                        int col = i % cols;

                        var frame = gif.Frames.CloneFrame(i);
                        spriteSheet.Mutate(ctx => ctx.DrawImage(frame, new Point(col * frameWidth, row * frameHeight), 1f));
                    }

                    // Save spritesheet
                    string tempFile = Path.GetTempFileName();
                    string spritePath = Path.ChangeExtension(tempFile, convert switch
                    {
                        TextureConversion.PNG => "png",
                        TextureConversion.JPEG => "jpg",
                        _ => "gif"
                    });

                    switch (convert)
                    {
                        case TextureConversion.PNG:
                            await spriteSheet.SaveAsPngAsync(spritePath);
                            break;
                        case TextureConversion.JPEG:
                            await spriteSheet.SaveAsJpegAsync(spritePath);
                            break;
                        default:
                            await spriteSheet.SaveAsGifAsync(spritePath);
                            break;
                    }

                    Uri localUri = await localDB.ImportLocalAssetAsync(spritePath, LocalDB.ImportLocation.Copy)
                        .ConfigureAwait(false);
                    File.Delete(spritePath);

                    await default(ToWorld);

                    targetSlot.Name = item.itemName;
                    if (forward.HasValue)
                        targetSlot.LocalRotation = floatQ.FromToRotation(forward.Value, float3.Forward);

                    StaticTexture2D tex = targetSlot.AttachComponent<StaticTexture2D>();
                    tex.URL.Value = localUri;
                    if (pointFiltering) tex.FilterMode.Value = TextureFilterMode.Point;
                    if (uncompressed)
                    {
                        tex.Uncompressed.Value = true;
                        tex.PowerOfTwoAlignThreshold.Value = 0f;
                    }

                    ImageImporter.SetupTextureProxyComponents(targetSlot, tex, stereoLayout, projection, setupScreenshotMetadata);
                    if (projection != 0)
                        ImageImporter.Create360Sphere(targetSlot, tex, stereoLayout, projection, addCollider);
                    else
                    {
                        while (!tex.IsAssetAvailable) await default(NextUpdate);
                        ImageImporter.CreateQuad(targetSlot, tex, stereoLayout, addCollider);
                    }

                    if (setupScreenshotMetadata)
                        targetSlot.GetComponentInChildren<PhotoMetadata>()?.NotifyModified();

                    // Attach Atlas & Animator components
                    AtlasInfo atlas = targetSlot.AttachComponent<AtlasInfo>();
                    UVAtlasAnimator animator = targetSlot.AttachComponent<UVAtlasAnimator>();
                    TimeIntDriver timer = targetSlot.AttachComponent<TimeIntDriver>();

                    atlas.GridFrames.Value = frameCount;
                    atlas.GridSize.Value = new int2(cols, rows);

                    int totalDelay = 0;
                    for (int i = 0; i < gif.Frames.Count; i++)
                    {
                        var frameMeta = gif.Frames[i].Metadata.GetGifMetadata();
                        int delay = frameMeta.FrameDelay;
                        if (delay <= 0) delay = 1;       
                        totalDelay += delay;             
                    }

                    float avgFrameDuration = totalDelay / (float)gif.Frames.Count / 100f;
                    timer.Scale.Value = 1f / avgFrameDuration;

                    timer.Repeat.Value = frameCount;
                    timer.Target.Target = animator.Frame;
                    animator.AtlasInfo.Target = atlas;

                    TextureSizeDriver texDriver = targetSlot.GetComponent<TextureSizeDriver>();
                    texDriver.Premultiply.Value = new float2(rows, cols);

                    UnlitMaterial mat = targetSlot.GetComponent<UnlitMaterial>();
                    animator.ScaleField.Target = mat.TextureScale;
                    animator.OffsetField.Target = mat.TextureOffset;
                    mat.BlendMode.Value = BlendMode.Cutout;

                    // Inventory preview shows first frame
                    ItemTextureThumbnailSource preview = targetSlot.GetComponent<ItemTextureThumbnailSource>();
                    preview.Crop.Value = new Rect(0, 0, 1f / cols, 1f / rows);

                    gif.Dispose();

                    return Result.Success();
                }
                catch (Exception ex)
                {
                    Error("Failed to read GIF: ", ex);
                    return Result.Failure(ex);
                }
            });

            return false;
        }
    }
}
