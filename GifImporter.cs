using System;
using System.IO;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using System.Drawing;

using HarmonyLib;
using ResoniteModLoader;

using FrooxEngine;
using FrooxEngine.Store;
using Elements.Core;
using System.Text;

namespace GifImporter;

public class GifImporter : ResoniteMod
    {
    public override string Name    => "GifImporter";
    public override string Author  => "astral";
    public override string Version => "1.1.9";
    public override string Link    => "https://github.com/astralchan/GifImporter";

    [AutoRegisterConfigKey]
    public static ModConfigurationKey<bool> KEY_SQUARE = new ModConfigurationKey<bool>(
        "Square spritesheet",
        "Generate square spritesheet",
        () => true);
    public static ModConfiguration? config;

    public override void OnEngineInit() {
        Harmony harmony = new Harmony("xyz.astralchan.gifimporter");
        harmony.PatchAll();
        config = GetConfiguration();
    }

    [HarmonyPatch(typeof(ImageImporter), "ImportImage")]
    class GifImporterPatch
        {
        public static bool Prefix(ref Task __result, ImportItem item, Slot targetSlot, float3? forward,
            StereoLayout stereoLayout, ImageProjection projection, bool setupScreenshotMetadata, bool addCollider,
            TextureConversion convert, bool pointFiltering) {
            Uri? uri = item.assetUri ?? (Uri.TryCreate(item.filePath, UriKind.Absolute, out var u) ? u : null);
            Image? image = null;
            bool validGif = false;

            LocalDB localDB = targetSlot.World.Engine.LocalDB;

            // Local file import vs URL import
            if (uri == null) {
                validGif = false;
            }
            else if (uri.Scheme == "file" && item.filePath != null) {
                // Check file header
                using (FileStream fs = new FileStream(item.filePath, FileMode.Open, FileAccess.Read)) {
                    byte[] headerBytes = new byte[6]; // GIF header is 6 bytes
                    int bytesRead = fs.Read(headerBytes, 0, headerBytes.Length);

                    if (bytesRead != headerBytes.Length) {
                        Debug("File is too short to be a gif");
                        return true;
                    }

                    string header = Encoding.ASCII.GetString(headerBytes);

                    if (header != "GIF87a" && header != "GIF89a") {
                        Debug("Magic number doesn't match GIF magic number");
                        return true;
                    }

                    validGif = true;
                }
                image = Image.FromStream(File.OpenRead(item.filePath));
            } else if (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "resdb") {
                var client = new System.Net.WebClient();
                image = Image.FromStream(client.OpenRead(uri));
                var type = client.ResponseHeaders.Get("content-type");
                validGif = type == "image/gif";
            } else if (uri.Scheme == "resdb") {
                validGif = true;
            }
            
            if (!validGif) {
                Debug($"{item.itemName} is not a gif, returning true");
                image?.Dispose();
                return true;
            }

            __result = targetSlot.StartTask(async delegate () {
                await default(ToBackground);

                // Load the image
                if (uri?.Scheme == "resdb") {
                    Debug($"Awaiting asset from resdb uri...");
                    image = Image.FromStream(await localDB.TryOpenAsset(uri));
                }

                int frameCount = 0;
                float frameDelay = 0;
                var frameWidth = 0;
                var frameHeight = 0;
                int gifRows = 0;
                int gifCols = 0;
                // https://docs.microsoft.com/en-us/dotnet/api/system.drawing.imaging.propertyitem.id PropertyTagFrameDelay
                const int PropertyTagFrameDelay = 0x5100;
                Bitmap? spriteSheet = null;
                string spritePath = Path.GetTempFileName();

                try {
                    frameCount = image!.GetFrameCount(FrameDimension.Time);

                    FrameDimension frameDimension = new FrameDimension(image.FrameDimensionsList[0]);
                    frameWidth = image.Width;
                    frameHeight = image.Height;

                    // Get the times stored in the image
                    var times = image.GetPropertyItem(PropertyTagFrameDelay).Value;

                    if (config!.GetValue(KEY_SQUARE)) {
                        // Calculate amount of cols and rows
                        float ratio = (float)frameWidth / frameHeight;
                        var cols = MathX.Sqrt(frameCount / ratio);
                        gifCols = MathX.RoundToInt(cols);
                        gifRows = frameCount / gifCols + ((frameCount % gifCols != 0) ? 1 : 0);
                    } else {
                        gifCols = frameCount;
                        gifRows = 1;
                    }

                    // Create a new image
                    spriteSheet = new Bitmap(frameWidth * gifCols, frameHeight * gifRows);
                    int delay = 0;
                    using (Graphics g = Graphics.FromImage(spriteSheet)) {
                        for (int i = 0; i < gifRows; i++)
                            for (int j = 0; j < gifCols; j++) {
                                if (i * gifCols + j >= frameCount)
                                    break;
                                // Convert 4-bit value to integer
                                var duration = BitConverter.ToInt32(times, 4 * ((i * gifCols) + j));
                                // Set the write frame before we save it
                                image.SelectActiveFrame(FrameDimension.Time, i * gifCols + j);
                                g.DrawImage(image, frameWidth * j, frameHeight * i);
                                delay += duration;
                            }
                        frameDelay = 100 * frameCount / delay;
                    }

                    // Save the image
                    ImageFormat saveFormat = spriteSheet.RawFormat;
                    switch (convert) {
                        case TextureConversion.PNG:
                            saveFormat = ImageFormat.Png;
                            break;
                        // Technically there should be a TextureConversion.WEBP here but there is no ImageFormat for it.
                        case TextureConversion.JPEG:
                            saveFormat = ImageFormat.Jpeg;
                            break;
                    }

                    spriteSheet.Save(spritePath, saveFormat);
                } finally {
                    image!.Dispose();
                }

                Debug($"Image saved as {spritePath}");

                Uri localUri = await localDB.ImportLocalAssetAsync(spritePath,
                    LocalDB.ImportLocation.Copy).ConfigureAwait(continueOnCapturedContext: false);

                File.Delete(spritePath);

                await default(ToWorld);

                targetSlot.Name = item.itemName;
                if (forward.HasValue) {
                    float3 from = forward.Value;
                    float3 to = float3.Forward;
                    targetSlot.LocalRotation = floatQ.FromToRotation(in from, in to);
                }

                StaticTexture2D tex = targetSlot.AttachComponent<StaticTexture2D>();
                tex.URL.Value = localUri;
                if (pointFiltering) {
                    tex.FilterMode.Value = TextureFilterMode.Point;
                }
                ImageImporter.SetupTextureProxyComponents(targetSlot, tex, stereoLayout, projection,
                    setupScreenshotMetadata);
                if (projection != 0)
                    ImageImporter.Create360Sphere(targetSlot, tex, stereoLayout, projection, addCollider);
                else {
                    while (!tex.IsAssetAvailable) await default(NextUpdate);
                    ImageImporter.CreateQuad(targetSlot, tex, stereoLayout, addCollider);
                }

                if (setupScreenshotMetadata)
                    targetSlot.GetComponentInChildren<PhotoMetadata>()?.NotifyOfScreenshot();

                AtlasInfo _AtlasInfo = targetSlot.AttachComponent<AtlasInfo>();
                UVAtlasAnimator _UVAtlasAnimator = targetSlot.AttachComponent<UVAtlasAnimator>();
                TimeIntDriver _TimeIntDriver = targetSlot.AttachComponent<TimeIntDriver>();
                _AtlasInfo.GridFrames.Value = frameCount;
                _AtlasInfo.GridSize.Value = new int2(gifCols, gifRows);
                _TimeIntDriver.Scale.Value = frameDelay;
                _TimeIntDriver.Repeat.Value = _AtlasInfo.GridFrames.Value;
                _TimeIntDriver.Target.Target = _UVAtlasAnimator.Frame;
                _UVAtlasAnimator.AtlasInfo.Target = _AtlasInfo;

                TextureSizeDriver _TextureSizeDriver = targetSlot.GetComponent<TextureSizeDriver>();
                _TextureSizeDriver.Premultiply.Value = new float2(gifRows, gifCols);

                UnlitMaterial _UnlitMaterial = targetSlot.GetComponent<UnlitMaterial>();
                _UVAtlasAnimator.ScaleField.Target = _UnlitMaterial.TextureScale;
                _UVAtlasAnimator.OffsetField.Target = _UnlitMaterial.TextureOffset;
                _UnlitMaterial.BlendMode.Value = BlendMode.Cutout;

                // Set inventory preview to first frame
                ItemTextureThumbnailSource _inventoryPreview = targetSlot.GetComponent<ItemTextureThumbnailSource>();
                _inventoryPreview.Crop.Value = new Rect(0, 0, 1f / (float)gifCols, 1f / (float)gifRows);
            });

            return false;
        }
    }
}
