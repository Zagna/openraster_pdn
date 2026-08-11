using PaintDotNet;
using PaintDotNet.Drawing;
using PaintDotNet.FileTypes;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace OpenRasterFileType {

    internal static class Modes {
        internal static readonly Dictionary<LayerBlendMode, string> PDN = new() {
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
            { LayerBlendMode.Xor, "svg:xor" },
        };

        internal static readonly Dictionary<string, LayerBlendMode> SVG = PDN.ToDictionary(x => x.Value, x => x.Key); 
    }

    public class OraFileType(IFileTypeHost host) : FileType(host, "OpenRaster", FileTypeOptions.Create() with {
            LoadExtensions = [".ora"],
            SaveExtensions = [".ora"],
            SupportsSavingLayers = true
        }) {
        
        protected override IFileTypeLoader OnCreateLoader() => new Loader(this);

        protected override IFileTypeSaver OnCreateSaver() => new Saver(this);

    }

    internal class Loader(OraFileType fileType) : FileTypeLoader(fileType) {
        protected override IFileTypeDocument OnLoad(IFileTypeLoadContext context) {
            using ZipArchive file = new(context.Input, ZipArchiveMode.Read);
            try {
                using StreamReader reader = new(file.GetEntry("mimetype").Open());
                if (!reader.ReadToEnd().Equals("image/openraster", StringComparison.Ordinal)) {
                    throw new FormatException("Incorrect mimetype: " + reader.ReadToEnd());
                }
            }
            catch (NullReferenceException) {
                throw new FormatException("No mimetype found in OpenRaster file");
            }
            
            XmlDocument stackXml = new();
            try {
                stackXml.Load(file.GetEntry("stack.xml").Open());
            }
            catch (NullReferenceException) {
                throw new FormatException("No 'stack.xml' found in OpenRaster file");
            }
            catch (XmlException) {
                throw new FormatException("Invalid XML file");
            }

            XmlElement imageElement = stackXml.DocumentElement;
            int width = int.Parse(imageElement.GetAttribute("w"), CultureInfo.InvariantCulture);
            int height = int.Parse(imageElement.GetAttribute("h"), CultureInfo.InvariantCulture);

            IFileTypeDocument document = context.Factory.CreateDocument(new(width, height), PixelFormats.Bgra32);
            document.Resolution = new(
                double.Parse(GetAttribute(imageElement, "xres", "72"), CultureInfo.InvariantCulture), 
                double.Parse(GetAttribute(imageElement, "yres", "72"), CultureInfo.InvariantCulture), 
                MeasurementUnit.Inch
            );
            
            XmlNodeList stackElements = stackXml.GetElementsByTagName("stack");

            if (stackElements.Count == 0) {
                throw new FormatException("No stack found in 'stack.xml'");
            }

            XmlElement stackElement = (XmlElement)stackElements[0];
            XmlNodeList layerElements = stackElement.GetElementsByTagName("layer");

            if (layerElements.Count == 0) {
                throw new FormatException("No layers found in OpenRaster file");
            }

            // IFileTypePropertyBag metadata = context.MetadataForSaveOptions;

            IImagingFactory imagingFactory = Services.GetService<IImagingFactory>();

            for (int i = layerElements.Count - 1; i >= 0; i--) { // The last layer in the list is the background so load in reverse
                XmlElement layerElement = (XmlElement)layerElements[i];
                int x = int.Parse(GetAttribute(layerElement, "x", "0"), CultureInfo.InvariantCulture);
                int y = int.Parse(GetAttribute(layerElement, "y", "0"), CultureInfo.InvariantCulture);

                Point2Int32 offset = new(x, y);

                using MemoryStream layerStream = new();
                try {
                    file.GetEntry(layerElement.GetAttribute("src")).Open().CopyTo(layerStream); 
                }
                catch (IOException) {
                    throw new FormatException("Missing layer file");
                }

                using IBitmapDecoder decoder = imagingFactory.CreateDecoder(layerStream);
                using IFileTypeBitmapLayer bitmapLayer = document.CreateBitmapLayer();
                IBitmapSource decoded = imagingFactory.CreateFormatConvertedBitmap(decoder.Frames[0], bitmapLayer.PixelFormat);

                SizeInt32 newSize = decoded.Size;
                if (offset.X + decoded.Size.Width > bitmapLayer.Size.Width) {
                    newSize.Width = bitmapLayer.Size.Width - offset.X;
                }
                if (offset.Y + decoded.Size.Height > bitmapLayer.Size.Height) {
                    newSize.Height = bitmapLayer.Size.Height - offset.Y;
                }

                if (decoded.Size != newSize) {
                    decoded = imagingFactory.CreateBitmapClipper(decoded, new(Point2Int32.Zero, newSize));
                }

                bitmapLayer.GetBitmap().WriteSource(offset, decoded);

                int layerNum = layerElements.Count - 1 - i;
                bitmapLayer.Name = GetAttribute(layerElement, "name", $"Layer {layerNum}");
                bitmapLayer.Opacity = float.Parse(GetAttribute(layerElement, "opacity", "1"), CultureInfo.InvariantCulture);
                bitmapLayer.Visible = GetAttribute(layerElement, "visibility", "visible") == "visible";

                XmlElement parentElement = (XmlElement)layerElement.ParentNode;
                float parentOpacity = float.Parse(GetAttribute(parentElement, "opacity", "1"), CultureInfo.InvariantCulture);
                if (parentOpacity != 1 && parentOpacity != bitmapLayer.Opacity) bitmapLayer.Opacity = parentOpacity;
                bool parentVisible =  GetAttribute(parentElement, "visibility", "visible") == "visible";
                if (!parentVisible && parentVisible != bitmapLayer.Visible) bitmapLayer.Visible = parentVisible;

                string compOp = GetAttribute(layerElement, "composite-op", "svg:src-over");
                compOp = compOp.Contains("pdn-") ? compOp.Replace("pdn-", "pdn:") : compOp;

                if (Modes.SVG.TryGetValue(compOp, out LayerBlendMode value)) {
                    bitmapLayer.BlendMode = value;
                }
                else {
                    string pdnCompOp = "pdn:" + compOp.Split(':')[1];
                    bitmapLayer.BlendMode = Modes.SVG.TryGetValue(pdnCompOp, out LayerBlendMode pdnValue) ? pdnValue : LayerBlendMode.Normal;
                }

                document.Layers.Insert(layerNum, bitmapLayer);
            }
            return document;
        }

        private static string GetAttribute(XmlElement element, string attribute, string defValue) {
            return element.HasAttribute(attribute) ? element.GetAttribute(attribute) : defValue;
        }
    }

    internal class Saver(OraFileType fileType) : FileTypeSaver(fileType) {

        private readonly OraFileType fileType = fileType;

        private const int thumbMaxSize = 256;

        private readonly string mimeTypeZip = "UEsDBBQAAAAAAAAAIQDHmvCMEAAAABAAAAAIAAAAbWltZXR5cGVpbWFnZS9vcGVucmFzdGVyUEsBAhQDFAAAAAAAAAAhAMea8IwQAAAAEAAAAAgAAAAAAAAAAAAAAKSBAAAAAG1pbWV0eXBlUEsFBgAAAAABAAEANgAAADYAAAAAAA==";

        protected override void OnSave(IFileTypeSaveContext context) {
            ArgumentNullException.ThrowIfNull(context.Document);
            ArgumentNullException.ThrowIfNull(context.Output);

            byte[] zipBytes = Convert.FromBase64String(mimeTypeZip);

            context.Output.Write(zipBytes);

            using ZipArchive archive = new(context.Output, ZipArchiveMode.Update, true);

            Point[] points = new Point[context.Document.Layers.Count];

            using IFileTypeCompositeBitmap<ColorBgra32> compositeBitmap = context.Document.GetCompositeBitmap<ColorBgra32>();

            foreach (var (layer, i) in context.Document.Layers.Select((value, i) => (value, i))) {
                RectInt32 bounds = compositeBitmap.Bounds();
                Color pixel;

                Bitmap layerBitMap = layer.GetBitmap().ToGdipBitmap();

                int left = layer.Size.Width;
                int top = layer.Size.Height;
                int right = 0;
                int bottom = 0;
                for (int y = 0; y < layer.Size.Height; y++) {
                    for (int x = 0; x < layer.Size.Width; x++) {
                        pixel = layerBitMap.GetPixel(x, y);
                        if (pixel.A > 0) {
                            left = x < left ? x : left;
                            right = x > right ? x : right;
                            top = y < top ? y : top;
                            bottom = y > bottom ? y : bottom;
                        }
                    }
                }

                if (left < layer.Size.Width && top < layer.Size.Height) { // is the layer not empty
                    bounds = new Rectangle(left, top, right - left + 1, bottom - top + 1); // clip it to the visible rectangle
                    points[i] = new Point(left, top);
                }
                else {
                    points[i] = Point.Empty;
                }

                using Stream pngStream = archive.CreateEntry("data/layer" + i.ToString(CultureInfo.InvariantCulture) + ".png").Open();
                layerBitMap.Clone(bounds, layerBitMap.PixelFormat).Save(pngStream, ImageFormat.Png);
            }

            using Stream sXML = archive.CreateEntry("stack.xml").Open();
            sXML.Write(GetLayerXmlData(context.Document, points));

            using Stream merge = archive.CreateEntry("mergedimage.png").Open();
            compositeBitmap.ToGdipBitmap().Save(merge, ImageFormat.Png);

            using Stream thumbStream = archive.CreateEntry("Thumbnails/thumbnail.png").Open();
            new Bitmap(compositeBitmap.ToGdipBitmap(), GetThumbDimensions(context.Document.Size.Width, context.Document.Size.Height)).Save(thumbStream, ImageFormat.Png);
        }

        private static byte[] GetLayerXmlData(IReadOnlyFileTypeDocument doc, Point[] points) {
            using MemoryStream xmlStream = new();

            XmlWriter writer = XmlWriter.Create(xmlStream, new XmlWriterSettings() {
                Indent = true,
                OmitXmlDeclaration = false,
                ConformanceLevel = ConformanceLevel.Document,
                CloseOutput = false
            });

            writer.WriteStartDocument();

            writer.WriteStartElement("image");
            writer.WriteAttributeString("w", doc.Size.Width.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("h", doc.Size.Height.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("version", "0.0.3"); // mandatory
            writer.WriteAttributeString("xres", doc.Resolution.X.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("yres", doc.Resolution.Y.ToString(CultureInfo.InvariantCulture));

            writer.WriteStartElement("stack");
            writer.WriteAttributeString("name", "root");

            // ORA stores layers top to bottom
            for (int i = doc.Layers.Count - 1; i >= 0; i--) {
                writer.WriteStartElement("layer");

                writer.WriteAttributeString("name", doc.Layers[i].Name);
                writer.WriteAttributeString("opacity", doc.Layers[i].Opacity.ToString(CultureInfo.InvariantCulture));

                writer.WriteAttributeString("src", "data/layer" + i.ToString(CultureInfo.InvariantCulture) + ".png");
                writer.WriteAttributeString("visibility", doc.Layers[i].Visible ? "visible" : "hidden");

                writer.WriteAttributeString("x", points[i].X.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("y", points[i].Y.ToString(CultureInfo.InvariantCulture));

                if (Modes.PDN.TryGetValue(doc.Layers[i].BlendMode, out string value)) {
                    writer.WriteAttributeString("composite-op", value);
                }
                else {
                    writer.WriteAttributeString("composite-op", "svg:src-over");
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // stack
            writer.WriteEndElement(); // image
            writer.WriteEndDocument();

            writer.Close();

            return xmlStream.ToArray();
        }

        private static Size GetThumbDimensions(int width, int height) {
            return width <= thumbMaxSize && height <= thumbMaxSize
                ? new Size(width, height)
                : width > height
                ? new Size(thumbMaxSize, (int)((double)height / width * thumbMaxSize))
                : new Size((int)((double)width / height * thumbMaxSize), thumbMaxSize);
        }
    }

    public class OraFileTypeFactory : IFileTypeFactory
    {
        public IFileType[] CreateFileTypes(IFileTypeHost host) => [new OraFileType(host)];
    }
}