﻿using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace ZombieLand
{
	[StaticConstructorOnStartup]
	public static class GraphicsDatabase
	{
		static readonly string textureRoot = Tools.GetModRootDirectory() + Path.DirectorySeparatorChar + "Textures" + Path.DirectorySeparatorChar;

		public static List<string> zombieRGBSkinColors = new List<string>();
		public static Graphic twinkieGraphic;

		static readonly Dictionary<string, ColorData> colorDataDatabase = new Dictionary<string, ColorData>();

		static GraphicsDatabase()
		{
			ReadSkinColors();

			TextureAtlas.AllImages.Do(item =>
			{
				var path = item.path;
				var width = item.data.width;
				var height = item.data.height;
				var originalPixels = item.data.pixels;

				if (path.StartsWith("Zombie/", System.StringComparison.Ordinal))
				{
					if (zombieRGBSkinColors.Count == 0)
						throw new Exception("zombieRGBSkinColors not initialized");

					zombieRGBSkinColors.Do(hex =>
					{
						var data = GetColoredData(originalPixels, hex.HexColor(), width, height);
						colorDataDatabase.Add(path + "#" + hex, data);
					});

					// add toxic green
					{
						var data = GetColoredData(originalPixels, Color.green, width, height);
						colorDataDatabase.Add(path + "#toxic", data);
					}

					// add miner dark
					{
						var pixels = originalPixels.Clone() as Color[];
						for (var i = 0; i < pixels.Length; i++)
						{
							pixels[i].r /= 4f;
							pixels[i].g /= 4f;
							pixels[i].b /= 4f;
						}
						var data = new ColorData(width, height, pixels);
						colorDataDatabase.Add(path + "#miner", data);
					}

					// add electric cyan
					{
						var data = GetColoredData(originalPixels, new Color(0.196078431f, 0.470588235f, 0.470588235f), width, height);
						colorDataDatabase.Add(path + "#electric", data);
					}

					// add albino white
					{
						var data = GetColoredData(originalPixels, Color.white, width, height);
						colorDataDatabase.Add(path + "#albino", data);
					}

					// add dark slimer black
					{
						var pixels = originalPixels.Clone() as Color[];
						for (var i = 0; i < pixels.Length; i++)
						{
							pixels[i].r /= 8f;
							pixels[i].g /= 8f;
							pixels[i].b /= 8f;
						}
						var data = new ColorData(width, height, pixels);
						colorDataDatabase.Add(path + "#dark", data);
					}
				}
				else
				{
					var data = new ColorData(width, height, originalPixels);
					colorDataDatabase.Add(path, data);
				}
			});

			var graphicData = new GraphicData()
			{
				shaderType = ShaderTypeDefOf.Cutout,
				texPath = "Twinkie",
				graphicClass = typeof(Graphic_Single)
			};
			twinkieGraphic = graphicData.Graphic;
		}

		static ColorData GetColoredData(Color[] data, Color color, int width, int height)
		{
			var pixels = data.Clone() as Color[];
			for (var i = 0; i < pixels.Length; i++)
			{
				// Linear burn gives best coloring results
				//
				Tools.ColorBlend(ref pixels[i].r, color.r);
				Tools.ColorBlend(ref pixels[i].g, color.g);
				Tools.ColorBlend(ref pixels[i].b, color.b);
			}

			return new ColorData(width, height, pixels);
		}

		static void ReadSkinColors()
		{
			var colors = new Texture2D(1, 1, TextureFormat.ARGB32, false);
			if (colors.LoadImage(File.ReadAllBytes(textureRoot + "SkinColors.png")) == false)
				throw new Exception("Cannot read SkinColors");

			var w = colors.width / 9;
			var h = colors.height / 9;
			for (var x = 1; x <= 7; x += 2)
				for (var y = 1; y <= 7; y += 2)
				{
					var c = colors.GetPixel(x * w, y * h);
					var hexColor = string.Format("{0:x02}{1:x02}{2:x02}", (int)(c.r * 255), (int)(c.g * 255), (int)(c.b * 255));
					zombieRGBSkinColors.Add(hexColor);
				}
		}

		public static Texture2D ToTexture(this ColorData data)
		{
			var texture = new Texture2D(data.width, data.height, TextureFormat.ARGB32, false);
			texture.SetPixels(data.pixels);
			texture.Apply(true, true);
			return texture;
		}

		public static ColorData GetColorData(string path, string color, bool makeCopy = false)
		{
			var key = color == null ? path : path + "#" + color;
			if (colorDataDatabase.TryGetValue(key, out var data) == false)
			{
				Log.Error("Cannot find preloaded texture path '" + path + (color == null ? "" : "' for color '" + color + "'"));
				return null;
			}
			return makeCopy ? data.Clone() : data;
		}

		public static Texture2D GetTexture(string path)
		{
			if (colorDataDatabase.TryGetValue(path, out var data) == false)
			{
				Log.Error("Cannot find preloaded texture path '" + path);
				return null;
			}
			return data.ToTexture();
		}
	}
}
