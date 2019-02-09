// paint.net OpenRaster Format Plugin
// 
// Copyright (c) 2017 Zagna https://github.com/Zagna & Nicholas Hayes https://github.com/0xC0000054
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using PaintDotNet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace OpenRasterFileType
	{
	public class OraFileType: FileType
		{
		private const int ThumbMaxSize = 256;

		// Base64 encoded .zip file containing just a uncompressed file called mimetype for OpenRaster.
		private readonly string MimeTypeZip = "UEsDBBQAAAAAAAAAIQDHmvCMEAAAABAAAAAIAAAAbWltZXR5cGVpbWFnZS9vcGVucmFzdGVyUEsBAhQDFAAAAAAAAAAhAMea8IwQAAAAEAAAAAgAAAAAAAAAAAAAAKSBAAAAAG1pbWV0eXBlUEsFBgAAAAABAAEANgAAADYAAAAAAA==";

		private static Dictionary<LayerBlendMode, string> BlendDict = new Dictionary<LayerBlendMode, string>()
			{
			{ LayerBlendMode.Normal, "svg:src-over" },
			{ LayerBlendMode.Multiply, "svg:multiply" },
			{ LayerBlendMode.Additive, "svg:plus" },
			{ LayerBlendMode.ColorBurn, "svg:color-burn" },
			{ LayerBlendMode.ColorDodge, "svg:color-dodge" },
			{ LayerBlendMode.Reflect, "pdn:reflect" },
			{ LayerBlendMode.Glow, "pdn:glow" },
			{ LayerBlendMode.Overlay, "svg:overlay" },
			{ LayerBlendMode.Difference, "svg:difference" },
			{ LayerBlendMode.Negation, "pdn:negation" },
			{ LayerBlendMode.Lighten, "svg:lighten" },
			{ LayerBlendMode.Darken, "svg:darken" },
			{ LayerBlendMode.Screen, "svg:screen" },
			{ LayerBlendMode.Xor, "svg:xor" }
			};

		private static readonly Dictionary<string, LayerBlendMode> SVGDict = BlendDict.ToDictionary(x => x.Value, x => x.Key);

		public OraFileType()
			: base("OpenRaster", FileTypeFlags.SupportsLoading | FileTypeFlags.SupportsSaving | FileTypeFlags.SupportsLayers, new String[] { ".ora" })
			{
			StrokeMapVersions = new string[2] { "mypaint_strokemap", "mypaint_strokemap_v2" };
			}

		/// <summary>
		/// Gets the bitmap from the ora layer.
		/// </summary>
		/// <param name="xofs">The x offset of the layer image.</param>
		/// <param name="yofs">The y offset of the layer image.</param>
		/// <param name="inStream">The input stream containing the layer image.</param>
		/// <param name="baseWidth">The width of the base document.</param>
		/// <param name="baseHeight">The height of the base document.</param>
		private unsafe Bitmap getBitmapFromOraLayer(int xofs, int yofs, Stream inStream, int baseWidth, int baseHeight)
			{
			Bitmap Image = null;

			using (Bitmap Layer = new Bitmap(baseWidth, baseHeight))
				{
				using (Bitmap BMP = new Bitmap(inStream))
					{
					BitmapData LayerData = Layer.LockBits(new Rectangle(xofs, yofs, BMP.Width, BMP.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
					BitmapData BMPData = BMP.LockBits(new Rectangle(0, 0, BMP.Width, BMP.Height), ImageLockMode.ReadOnly, BMP.PixelFormat);

					int bpp = Bitmap.GetPixelFormatSize(BMP.PixelFormat) / 8;

					for (int y = 0; y < BMP.Height; y++)
						{
						for (int x = 0; x < BMP.Width; x++)
							{
							byte* dst = (byte*)LayerData.Scan0.ToPointer() + (y * LayerData.Stride) + (x * 4);
							byte* src = (byte*)BMPData.Scan0.ToPointer() + (y * BMPData.Stride) + (x * bpp);

							dst[0] = src[0]; // B
							dst[1] = src[1]; // G
							dst[2] = src[2]; // R

							if (bpp == 4)
								{
								dst[3] = src[3]; // A
								}
							else
								{
								dst[3] = 255;
								}
							}
						}
					BMP.UnlockBits(BMPData);
					Layer.UnlockBits(LayerData);
					}
				Image = (Bitmap)Layer.Clone();
				}
			return Image;
			}

		/// <summary>
		/// The formats for MyPaint's stroke map, version 1 and version 2.
		/// </summary>
		private readonly string[] StrokeMapVersions;

		protected override Document OnLoad(Stream input)
			{
			using (ZipArchive File = new ZipArchive(input, ZipArchiveMode.Read))
				{
				try
					{
					string MimeType;

					ZipArchiveEntry MimeEntry = File.GetEntry("mimetype");
					using (StreamReader Reader = new StreamReader(MimeEntry.Open()))
						{
						MimeType = Reader.ReadToEnd();
						}

					if (!MimeType.Equals("image/openraster", StringComparison.Ordinal))
						throw new FormatException("Incorrect mimetype: " + MimeType);
					}
				catch (NullReferenceException)
					{
					throw new FormatException("No mimetype found in OpenRaster file");
					}

				XmlDocument stackXml = new XmlDocument();
				stackXml.Load(File.GetEntry("stack.xml").Open());

				XmlElement ImageElement = stackXml.DocumentElement;
				int Width = int.Parse(ImageElement.GetAttribute("w"), CultureInfo.InvariantCulture);
				int Height = int.Parse(ImageElement.GetAttribute("h"), CultureInfo.InvariantCulture);

				Document doc = new Document(Width, Height)
					{
					DpuUnit = MeasurementUnit.Inch,
					DpuX = double.Parse(getAttribute(ImageElement, "xres", "72"), CultureInfo.InvariantCulture),
					DpuY = double.Parse(getAttribute(ImageElement, "yres", "72"), CultureInfo.InvariantCulture)
					};

				XmlElement stackElement = (XmlElement)stackXml.GetElementsByTagName("stack")[0];
				XmlNodeList LayerElements = stackElement.GetElementsByTagName("layer");

				if (LayerElements.Count == 0)
					throw new FormatException("No layers found in OpenRaster file");
				int LayerCount = LayerElements.Count - 1;

				for (int i = LayerCount; i >= 0; i--) // The last layer in the list is the background so load in reverse
					{
					XmlElement LayerElement = (XmlElement)LayerElements[i];
					int x = int.Parse(getAttribute(LayerElement, "x", "0"), CultureInfo.InvariantCulture); // the x offset within the layer
					int y = int.Parse(getAttribute(LayerElement, "y", "0"), CultureInfo.InvariantCulture); // the y offset within the layer

					int LayerNum = LayerCount - i;

					string name = getAttribute(LayerElement, "name", string.Format("Layer {0}", LayerNum, CultureInfo.InvariantCulture));

					ZipArchiveEntry zf = File.GetEntry(LayerElement.GetAttribute("src"));

					using (Stream s = zf.Open())
						{
						using (Bitmap BMP = getBitmapFromOraLayer(x, y, s, Width, Height))
							{
							BitmapLayer myLayer = null;

							if (i == LayerCount) // load the background layer first
								{
								myLayer = Layer.CreateBackgroundLayer(Width, Height);
								}
							else
								{
								myLayer = new BitmapLayer(Width, Height);
								}

							myLayer.Name = name;
							myLayer.Opacity = ((byte)(255.0 * double.Parse(getAttribute(LayerElement, "opacity", "1"), CultureInfo.InvariantCulture)));
							myLayer.Visible = (getAttribute(LayerElement, "visibility", "visible") == "visible"); // newer ora files have this

							string compop = getAttribute(LayerElement, "composite-op", "svg:src-over");

							try
								{
								myLayer.BlendMode = SVGDict[compop];
								}
							catch (KeyNotFoundException)
								{
								try
									{
									string[] compops = compop.Split(':');
									myLayer.BlendMode = SVGDict["pdn:" + compops[1]];
									}
								catch (KeyNotFoundException)
									{
									myLayer.BlendMode = LayerBlendMode.Normal;
									}
								}

							myLayer.Surface.CopyFromGdipBitmap(BMP, false); // does this make sense?

							string backTile = getAttribute(LayerElement, "background_tile", string.Empty);

							if (!string.IsNullOrEmpty(backTile))
								{
								ZipArchiveEntry tileZf = File.GetEntry(backTile);
								byte[] tileBytes = null;
								using (Stream tileStream = tileZf.Open())
									{
									tileBytes = new byte[(int)tileStream.Length];

									int numBytesToRead = (int)tileStream.Length;
									int numBytesRead = 0;
									while (numBytesToRead > 0)
										{
										// Read may return anything from 0 to numBytesToRead.
										int n = tileStream.Read(tileBytes, numBytesRead, numBytesToRead);
										// The end of the file is reached.
										if (n == 0)
											{
											break;
											}
										numBytesRead += n;
										numBytesToRead -= n;
										}
									}

								string tileData = Convert.ToBase64String(tileBytes);
								// convert the tile image to a Base64String and then save it in the layer's MetaData.
								myLayer.Metadata.SetUserValue("OraBackgroundTile", tileData);
								}

							foreach (string version in StrokeMapVersions)
								{
								string strokeMap = getAttribute(LayerElement, version, string.Empty);

								if (!string.IsNullOrEmpty(strokeMap))
									{
									ZipArchiveEntry strokeZf = File.GetEntry(strokeMap);
									byte[] strokeBytes = null;
									using (Stream strokeStream = strokeZf.Open())
										{
										strokeBytes = new byte[(int)strokeStream.Length];

										int numBytesToRead = (int)strokeStream.Length;
										int numBytesRead = 0;
										while (numBytesToRead > 0)
											{
											// Read may return anything from 0 to numBytesToRead.
											int n = strokeStream.Read(strokeBytes, numBytesRead, numBytesToRead);
											// The end of the file is reached.
											if (n == 0)
												{
												break;
												}
											numBytesRead += n;
											numBytesToRead -= n;
											}
										}
									string strokeData = Convert.ToBase64String(strokeBytes);
									// convert the stroke map to a Base64String and then save it in the layer's MetaData.

									myLayer.Metadata.SetUserValue("OraMyPaintStrokeMapData", strokeData);

									// Save the version of the stroke map in the MetaData
									myLayer.Metadata.SetUserValue("OraMyPaintStrokeMapVersion", version);
									}
								}
							doc.Layers.Insert(LayerNum, myLayer);
							}
						}
					}
				return doc;
				}
			}

		// A struct to store the new x,y offsets
		private struct LayerInfo
			{
			public int x;
			public int y;

			public LayerInfo(int x, int y)
				{
				this.x = x;
				this.y = y;
				}
			}

		protected override void OnSave(Document input, Stream output, SaveConfigToken token, Surface scratchSurface, ProgressEventHandler callback)
			{
			byte[] DataBytes = Convert.FromBase64String(MimeTypeZip);
			output.Write(DataBytes, 0, DataBytes.Length);

			using (ZipArchive Archive = new ZipArchive(output, ZipArchiveMode.Update, true))
				{

				LayerInfo[] LayerInfo = new LayerInfo[input.Layers.Count];

				for (int i = 0; i < input.Layers.Count; i++)
					{
					BitmapLayer Layer = (BitmapLayer)input.Layers[i];
					Rectangle Bounds = Layer.Surface.Bounds;

					int Left = Layer.Width;
					int Top = Layer.Height;
					int Right = 0;
					int Bottom = 0;
					unsafe
						{
						for (int y = 0; y < Layer.Height; y++)
							{
							ColorBgra* row = Layer.Surface.GetRowAddress(y);
							ColorBgra* pixel = row;

							for (int x = 0; x < Layer.Width; x++)
								{
								if (pixel->A > 0)
									{
									if (x < Left)
										{
										Left = x;
										}
									if (x > Right)
										{
										Right = x;
										}
									if (y < Top)
										{
										Top = y;
										}
									if (y > Bottom)
										{
										Bottom = y;
										}
									}
								pixel++;
								}
							}
						}

					if (Left < Layer.Width && Top < Layer.Height) // is the layer not empty
						{
						Bounds = new Rectangle(Left, Top, (Right - Left) + 1, (Bottom - Top) + 1); // clip it to the visible rectangle
						LayerInfo[i] = new LayerInfo(Left, Top);
						}
					else
						{
						LayerInfo[i] = new LayerInfo(0, 0);
						}

					string tileData = Layer.Metadata.GetUserValue("OraBackgroundTile");

					if (!string.IsNullOrEmpty(tileData)) // save the background_tile png if it exists
						{
						ZipArchiveEntry bgTile = Archive.CreateEntry("data/background_tile.png");

						using (Stream Streamy = bgTile.Open())
							{
							byte[] tileBytes = Convert.FromBase64String(tileData);
							Streamy.Write(tileBytes, 0, tileBytes.Length);
							}
						}

					string strokeData = Layer.Metadata.GetUserValue("OraMyPaintStrokeMapData");

					if (!string.IsNullOrEmpty(strokeData)) // save MyPaint's stroke data if it exists
						{
						ZipArchiveEntry strokeMap = Archive.CreateEntry("data/layer" + i.ToString(CultureInfo.InvariantCulture) + "_strokemap.dat");

						using (Stream Streamy = strokeMap.Open())
							{
							byte[] tileBytes = Convert.FromBase64String(strokeData);
							Streamy.Write(tileBytes, 0, tileBytes.Length);
							}
						}

					byte[] buf = null;

					using (MemoryStream ms = new MemoryStream())
						{
						Layer.Surface.CreateAliasedBitmap(Bounds, true).Save(ms, ImageFormat.Png);
						buf = ms.ToArray();
						}

					ZipArchiveEntry layerpng = Archive.CreateEntry("data/layer" + i.ToString(CultureInfo.InvariantCulture) + ".png");

					using (Stream Streamy = layerpng.Open())
						{
						Streamy.Write(buf, 0, buf.Length);
						}
					}

				ZipArchiveEntry stackxml = Archive.CreateEntry("stack.xml");

				using (Stream Streamy = stackxml.Open())
					{
					double dpiX;
					double dpiY;

					switch (input.DpuUnit)
						{
						case MeasurementUnit.Centimeter:
							dpiX = Document.DotsPerCmToDotsPerInch(input.DpuX);
							dpiY = Document.DotsPerCmToDotsPerInch(input.DpuY);
							break;

						case MeasurementUnit.Inch:
							dpiX = input.DpuX;
							dpiY = input.DpuY;
							break;

						case MeasurementUnit.Pixel:
							dpiX = Document.GetDefaultDpu(MeasurementUnit.Inch);
							dpiY = Document.GetDefaultDpu(MeasurementUnit.Inch);
							break;

						default:
							throw new InvalidEnumArgumentException();
						}

					DataBytes = getLayerXmlData(input.Layers, LayerInfo, dpiX, dpiY);
					Streamy.Write(DataBytes, 0, DataBytes.Length);
					}

				using (Surface Flat = new Surface(input.Width, input.Height))
					{
					input.Flatten(Flat);

					using (MemoryStream ms = new MemoryStream())
						{
						Flat.CreateAliasedBitmap().Save(ms, ImageFormat.Png);
						DataBytes = ms.ToArray();
						}

					ZipArchiveEntry Mergy = Archive.CreateEntry("mergedimage.png");

					using (Stream Streamy = Mergy.Open())
						{
						Streamy.Write(DataBytes, 0, DataBytes.Length);
						}

					Size thumbSize = getThumbDimensions(input.Width, input.Height);

					Surface Scale = new Surface(thumbSize);
					Scale.FitSurface(ResamplingAlgorithm.SuperSampling, Flat);

					using (MemoryStream ms = new MemoryStream())
						{
						Scale.CreateAliasedBitmap().Save(ms, ImageFormat.Png);
						DataBytes = ms.ToArray();
						}
					Scale.Dispose();

					ZipArchiveEntry Thumbsy = Archive.CreateEntry("Thumbnails/thumbnail.png");

					using (Stream Streamy = Thumbsy.Open())
						{
						Streamy.Write(DataBytes, 0, DataBytes.Length);
						}
					}
				}

			System.Diagnostics.Debug.WriteLine("All done here");
			}

		private byte[] getLayerXmlData(LayerList layers, LayerInfo[] info, double dpiX, double dpiY) // OraFormat.cs - some changes
			{
			byte[] buf = null;

			using (MemoryStream ms = new MemoryStream())
				{
				XmlWriterSettings Settings = new XmlWriterSettings
					{
					Indent = true,
					OmitXmlDeclaration = false,
					ConformanceLevel = ConformanceLevel.Document,
					CloseOutput = false
					};
				XmlWriter Writer = XmlWriter.Create(ms, Settings);

				Writer.WriteStartDocument();

				Writer.WriteStartElement("image");
				Writer.WriteAttributeString("w", layers.GetAt(0).Width.ToString(CultureInfo.InvariantCulture));
				Writer.WriteAttributeString("h", layers.GetAt(0).Height.ToString(CultureInfo.InvariantCulture));
				Writer.WriteAttributeString("version", "0.0.3"); // mandatory

				Writer.WriteAttributeString("xres", dpiX.ToString(CultureInfo.InvariantCulture));
				Writer.WriteAttributeString("yres", dpiY.ToString(CultureInfo.InvariantCulture));

				Writer.WriteStartElement("stack");
				Writer.WriteAttributeString("name", "root");

				// ORA stores layers top to bottom
				for (int i = layers.Count - 1; i >= 0; i--)
					{
					BitmapLayer layer = (BitmapLayer)layers[i];

					Writer.WriteStartElement("layer");

					string backTile = layer.Metadata.GetUserValue("OraBackgroundTile");

					if (!string.IsNullOrEmpty(backTile))
						{
						Writer.WriteAttributeString("background_tile", "data/background_tile.png");
						}
					string strokeMapVersion = layer.Metadata.GetUserValue("OraMyPaintStrokeMapVersion");

					if (!string.IsNullOrEmpty(strokeMapVersion))
						{
						Writer.WriteAttributeString(strokeMapVersion, "data/layer" + i.ToString(CultureInfo.InvariantCulture) + "_strokemap.dat");
						}
					if (string.IsNullOrEmpty(strokeMapVersion)) // the stroke map layer does not have a name
						{
						Writer.WriteAttributeString("name", layer.Name);
						}

					Writer.WriteAttributeString("opacity", (layer.Opacity / 255.0).Clamp(0.0, 1.0).ToString("N2", CultureInfo.InvariantCulture)); // this is even more bizarre :D

					Writer.WriteAttributeString("src", "data/layer" + i.ToString(CultureInfo.InvariantCulture) + ".png");
					Writer.WriteAttributeString("visibility", layer.Visible ? "visible" : "hidden");

					Writer.WriteAttributeString("x", info[i].x.ToString(CultureInfo.InvariantCulture));
					Writer.WriteAttributeString("y", info[i].y.ToString(CultureInfo.InvariantCulture));
					try
						{
						Writer.WriteAttributeString("composite-op", BlendDict[layer.BlendMode]);
						}
					catch (KeyNotFoundException)
						{
						Writer.WriteAttributeString("composite-op", "svg:src-over");
						}

					Writer.WriteEndElement();
					}

				Writer.WriteEndElement(); // stack
				Writer.WriteEndElement(); // image
				Writer.WriteEndDocument();

				Writer.Close();

				buf = ms.ToArray();
				}
			return buf;
			}

		private Size getThumbDimensions(int width, int height) // OraFormat.cs
			{
			if (width <= ThumbMaxSize && height <= ThumbMaxSize)
				return new Size(width, height);

			if (width > height)
				return new Size(ThumbMaxSize, (int)((double)height / width * ThumbMaxSize));
			else
				return new Size((int)((double)width / height * ThumbMaxSize), ThumbMaxSize);
			}

		private static string getAttribute(XmlElement element, string attribute, string defValue) // OraFormat.cs
			{
			string ret = element.GetAttribute(attribute);
			return string.IsNullOrEmpty(ret) ? defValue : ret;
			}
		}

	public class MyFileTypeFactory: IFileTypeFactory
		{
		public FileType[] GetFileTypeInstances()
			{
			return new FileType[] { new OraFileType() };
			}
		}
	}