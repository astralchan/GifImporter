using HarmonyLib;
using NeosModLoader;
using FrooxEngine;
using BaseX;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing;

namespace GifImporter
{
    /*
     * [____CURSOR PARKING LOT_______]
     * [                             ]
     * [_____________________________]
     *  EDIT: this was important when we were in live share
     * 	Users present at one point: art0007i, sls, amber, dfgHiatus, MilkySenpai
     */
    public class GifImporter : NeosMod
    {
        public override string Name => "GifImporter";
        public override string Author => "amber";
        public override string Version => "1.1.1";
        public override string Link => "https://github.com/kawaiiamber/GifImporter";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("tk.kawaiiamber.gifimporter");
            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(ImageImporter), "ImportImage")]
        class FileImporterPatch
        {
            public static bool Prefix(string path, ref Task __result, Slot targetSlot, float3? forward, StereoLayout stereoLayout, ImageProjection projection, bool setupScreenshotMetadata, bool addCollider)
            {
                Uri uri = new Uri(path);
                Image image = null;
                bool validGif = false;
                // Local file import vs URL import
                if (uri.Scheme == "file" && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    image = Image.FromStream(File.OpenRead(path));
                    validGif = true;
                }
                else if (uri.Scheme == "http" || uri.Scheme == "https")
                {
                    var client = new System.Net.WebClient();
                    image = Image.FromStream(client.OpenRead(uri));
                    var type = client.ResponseHeaders.Get("content-type");
                    if(type == "image/gif")
                    {
                        validGif = true;
                    }
                }
                /* TODO: Support neosdb links
				else if (uri.Scheme == "neosdb"){
					// neosdb handling here
				} */
                if (!validGif)
                {
                    Error(new ArgumentException($"Image is not a gif or the URI Scheme {uri.Scheme} is not supported"));
                    image?.Dispose();
                    return true;
                }
                __result = targetSlot.StartTask(async delegate ()
                {

                    await default(ToBackground);
                    // Load the image
                    int frameCount = 0;
                    float frameDelay = 0;
                    var frameWidth = 0;
                    var frameHeight = 0;
                    const int PropertyTagFrameDelay = 0x5100; // https://docs.microsoft.com/en-us/dotnet/api/system.drawing.imaging.propertyitem.id PropertyTagFrameDelay
                    Bitmap spriteSheet = null;
                    string spritePath = Path.Combine(Engine.Current.AppPath, "nml_mods", "tmp_sheet.png");

                    try
                    {

                        frameCount = image.GetFrameCount(FrameDimension.Time);

                        FrameDimension frameDimension = new FrameDimension(image.FrameDimensionsList[0]);
                        frameWidth = image.Width;
                        frameHeight = image.Height;
                        //Get the times stored in the image
                        var times = image.GetPropertyItem(PropertyTagFrameDelay).Value;

                        // Create a new image
                        spriteSheet = new Bitmap((int)(frameCount * frameWidth), (int)frameHeight);
                        int delay = 0;
                        using (Graphics g = Graphics.FromImage(spriteSheet))
                        {
                            for (int i = 0; i < frameCount; i++)
                            {
                                //convert 4 bit value to integer
                                var duration = BitConverter.ToInt32(times, 4 * i);
                                //Set the write frame before we save it
                                image.SelectActiveFrame(FrameDimension.Time, i);

                                g.DrawImage(image, frameWidth * i, 0);

                                delay += duration;
                            }
                            frameDelay = 100 / (delay / frameCount);
                        }

                        // Save the image
                        spriteSheet.Save(spritePath);
                    }
                    finally
                    {
                        image.Dispose();
                    }

                    Debug($"Image saved as {spritePath}");

                    LocalDB localDB = targetSlot.World.Engine.LocalDB;
                    Uri localUri = await localDB.ImportLocalAssetAsync(spritePath, LocalDB.ImportLocation.Copy).ConfigureAwait(continueOnCapturedContext: false);

                    File.Delete(spritePath);

                    await default(ToWorld);

                    targetSlot.Name = Path.GetFileNameWithoutExtension(spritePath);
                    if (forward.HasValue)
                    {
                        float3 from = forward.Value;
                        float3 to = float3.Forward;
                        targetSlot.LocalRotation = floatQ.FromToRotation(in from, in to);
                    }
                    StaticTexture2D tex = targetSlot.AttachComponent<StaticTexture2D>();
                    tex.URL.Value = localUri;
                    ImageImporter.SetupTextureProxyComponents(targetSlot, tex, stereoLayout, projection, setupScreenshotMetadata);
                    if (projection != 0)
                    {
                        ImageImporter.Create360Sphere(targetSlot, tex, stereoLayout, projection, addCollider);
                    }
                    else
                    {
                        while (!tex.IsAssetAvailable)
                        {
                            await default(NextUpdate);
                        }
                        ImageImporter.CreateQuad(targetSlot, tex, stereoLayout, addCollider);
                    }
                    if (setupScreenshotMetadata)
                    {
                        targetSlot.GetComponentInChildren<PhotoMetadata>()?.NotifyOfScreenshot();
                    }

                    AtlasInfo _AtlasInfo = targetSlot.AttachComponent<AtlasInfo>();
                    UVAtlasAnimator _UVAtlasAnimator = targetSlot.AttachComponent<UVAtlasAnimator>();
                    TimeIntDriver _TimeIntDriver = targetSlot.AttachComponent<TimeIntDriver>();
                    _AtlasInfo.GridFrames.Value = frameCount;
                    _AtlasInfo.GridSize.Value = new int2(frameCount, 1);
                    _TimeIntDriver.Scale.Value = frameDelay;
                    _TimeIntDriver.Repeat.Value = _AtlasInfo.GridFrames.Value;
                    _TimeIntDriver.Target.Target = _UVAtlasAnimator.Frame;
                    _UVAtlasAnimator.AtlasInfo.Target = _AtlasInfo;

                    QuadMesh _QuadMesh = targetSlot.GetComponent<QuadMesh>();
                    _QuadMesh.Size.Value = new float2(frameWidth, frameHeight).Normalized;

                    UnlitMaterial _UnlitMaterial = targetSlot.GetComponent<UnlitMaterial>();
                    _UVAtlasAnimator.ScaleField.Target = _UnlitMaterial.TextureScale;
                    _UVAtlasAnimator.OffsetField.Target = _UnlitMaterial.TextureOffset;
                });

                return false;
            }
        }
    }
}
