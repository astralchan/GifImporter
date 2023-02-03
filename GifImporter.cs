// vim: ts=4 sw=4 noet cc=120

using System;
using System.Threading.Tasks;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing;
using FrooxEngine;
using BaseX;
using HarmonyLib;
using NeosModLoader;

namespace GifImporter;
public class GifImporter : NeosMod
{
	public override string Name    => "GifImporter";
	public override string Author  => "amber";
	public override string Version => "1.1.3";
	public override string Link    => "https://github.com/astralchan/GifImporter";

	[AutoRegisterConfigKey]
	public static ModConfigurationKey<bool> KEY_SQUARE = new ModConfigurationKey<bool>(
		"Square spritesheet",
		"Generate square spritesheet (sometimes has bigger size)",
		() => false);
	public static ModConfiguration? config;

	public override void OnEngineInit() {
		Harmony harmony = new Harmony("xyz.astralchan.gifimporter");
		harmony.PatchAll();
		config = GetConfiguration();
	}

	[HarmonyPatch(typeof(ImageImporter), "ImportImage")]
	class GifImporterPatch
	{
		public static bool Prefix(string path, ref Task __result, Slot targetSlot, float3? forward,
			StereoLayout stereoLayout, ImageProjection projection, bool setupScreenshotMetadata, bool addCollider) {
			Uri uri = new Uri(path);
			Image? image = null;
			bool validGif = false;

			// Local file import vs URL import
			if (uri.Scheme == "file" && string.Equals(Path.GetExtension(path), ".gif",
				StringComparison.OrdinalIgnoreCase)) {
				image = Image.FromStream(File.OpenRead(path));
				validGif = true;
			} else if (uri.Scheme == "http" || uri.Scheme == "https") {
				var client = new System.Net.WebClient();
				image = Image.FromStream(client.OpenRead(uri));
				var type = client.ResponseHeaders.Get("content-type");
				validGif = type == "image/gif";
			}
			/* TODO: Support neosdb links
			else if (uri.Scheme == "neosdb"){
				// neosdb handling here
			} */
			if (!validGif) {
				Debug($"{path} is not a gif, returning true");
				image?.Dispose();
				return true;
			}
			__result = targetSlot.StartTask(async delegate () {
				await default(ToBackground);
				// Load the image
				int frameCount = 0;
				float frameDelay = 0;
				var frameWidth = 0;
				var frameHeight = 0;
				int gifRows = 0;
				int gifCols = 0;
				// https://docs.microsoft.com/en-us/dotnet/api/system.drawing.imaging.propertyitem.id PropertyTagFrameDelay
				const int PropertyTagFrameDelay = 0x5100;
				Bitmap? spriteSheet = null;
				string spritePath = Path.Combine(System.IO.Path.GetTempPath(), Path.GetFileName(path));

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
						for (int i = 0; i < gifRows; i++) for (int j = 0; j < gifCols; j++) {
							if (i * gifCols + j >= frameCount) break;
							//convert 4 bit value to integer
							var duration = BitConverter.ToInt32(times, 4 * ((i * gifCols) + j));
							//Set the write frame before we save it
							image.SelectActiveFrame(FrameDimension.Time, i * gifCols + j);
							g.DrawImage(image, frameWidth * j, frameHeight * i);
							delay += duration;
						}
						frameDelay = 100 * frameCount / delay;
					}

					// Save the image
					spriteSheet.Save(spritePath);
				}
				finally {
					image!.Dispose();
				}

				Debug($"Image saved as {spritePath}");

				LocalDB localDB = targetSlot.World.Engine.LocalDB;
				Uri localUri = await
					localDB.ImportLocalAssetAsync(spritePath,
					LocalDB.ImportLocation.Copy).ConfigureAwait(continueOnCapturedContext: false);

				File.Delete(spritePath);

				await default(ToWorld);

				targetSlot.Name = Path.GetFileNameWithoutExtension(spritePath);
				if (forward.HasValue) {
					float3 from = forward.Value;
					float3 to = float3.Forward;
					targetSlot.LocalRotation = floatQ.FromToRotation(in from, in to);
				}

				StaticTexture2D tex = targetSlot.AttachComponent<StaticTexture2D>();
				tex.URL.Value = localUri;
				ImageImporter.SetupTextureProxyComponents(targetSlot, tex, stereoLayout, projection,
					setupScreenshotMetadata);
				if (projection != 0)
					ImageImporter.Create360Sphere(targetSlot, tex, stereoLayout, projection, addCollider);
				else {
					while (!tex.IsAssetAvailable) await default(NextUpdate);
					ImageImporter.CreateQuad(targetSlot, tex, stereoLayout, addCollider);
				}

				if (setupScreenshotMetadata) targetSlot.GetComponentInChildren<PhotoMetadata>()?.NotifyOfScreenshot();

				AtlasInfo _AtlasInfo = targetSlot.AttachComponent<AtlasInfo>();
				UVAtlasAnimator _UVAtlasAnimator = targetSlot.AttachComponent<UVAtlasAnimator>();
				TimeIntDriver _TimeIntDriver = targetSlot.AttachComponent<TimeIntDriver>();
				_AtlasInfo.GridFrames.Value = frameCount;
				_AtlasInfo.GridSize.Value = new int2(gifCols, gifRows);
				_TimeIntDriver.Scale.Value = frameDelay;
				_TimeIntDriver.Repeat.Value = _AtlasInfo.GridFrames.Value;
				_TimeIntDriver.Target.Target = _UVAtlasAnimator.Frame;
				_UVAtlasAnimator.AtlasInfo.Target = _AtlasInfo;

				QuadMesh _QuadMesh = targetSlot.GetComponent<QuadMesh>();
				_QuadMesh.Size.Value = new float2(frameWidth, frameHeight).Normalized;

				UnlitMaterial _UnlitMaterial = targetSlot.GetComponent<UnlitMaterial>();
				_UVAtlasAnimator.ScaleField.Target = _UnlitMaterial.TextureScale;
				_UVAtlasAnimator.OffsetField.Target = _UnlitMaterial.TextureOffset;

				// Set inventory preview to first frame
				ItemTextureThumbnailSource _inventoryPreview = targetSlot.GetComponent<ItemTextureThumbnailSource>();
				_inventoryPreview.Crop.Value = new Rect(0, 0, 1f / (float)gifCols, 1f / (float)gifRows);
			});

			return false;
		}
	}
}