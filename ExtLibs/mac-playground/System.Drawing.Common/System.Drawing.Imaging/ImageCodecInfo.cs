//
// System.Drawing.Imaging.ImageCodecInfo.cs
//
// Authors:
//   Everaldo Canuto (everaldo.canuto@bol.com.br)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//   Dennis Hayes (dennish@raytek.com)
//   Jordi Mas i Hernandez (jordi@ximian.com)
//   Sebastien Pouliot  <sebastien@ximian.com>
//
// (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004,2006,2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Runtime.InteropServices;
using System.Collections;
using System.IO;

namespace System.Drawing.Imaging
{

	public sealed class ImageCodecInfo
	{
		internal ImageCodecInfo()
		{
		}

		// properties
		internal ImageFormat Format { get; set; } // This is a workaround

		public Guid Clsid { get; set; }
		public string CodecName { get; set; }
		public string DllName { get; set; }
		public string FilenameExtension { get; set; }
		public ImageCodecFlags Flags { get; set; }
		public string FormatDescription { get; set; }
		public Guid FormatID { get; set; }
		public string MimeType { get; set; }
		[CLSCompliant(false)]
		public byte[][] SignatureMasks { get; set; }
		[CLSCompliant(false)]
		public byte[][] SignaturePatterns { get; set; }
		public int Version { get; set; }


		// methods		
		// GDI+'s built-in codecs, with the identifiers, MIME types and extensions Windows reports
		// (code picks an encoder by MimeType, e.g. "image/jpeg"); Bitmap.Save writes them with ImageIO
		static ImageCodecInfo Codec(ImageFormat format, int clsid, string name, string extensions, string mime, ImageCodecFlags flags)
		{
			return new ImageCodecInfo
			{
				Format = format,
				Clsid = new Guid(clsid, 0x1a04, 0x11d3, 0x9a, 0x73, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e),
				FormatID = format.Guid,
				CodecName = "Built-in " + name + " Codec",
				FormatDescription = name,
				FilenameExtension = extensions,
				MimeType = mime,
				Flags = flags | ImageCodecFlags.Builtin | ImageCodecFlags.SupportBitmap,
				Version = 1,
			};
		}

		const ImageCodecFlags Both = ImageCodecFlags.Encoder | ImageCodecFlags.Decoder;

		public static ImageCodecInfo[] GetImageDecoders()
		{
			return new ImageCodecInfo[] {
				Codec(ImageFormat.Bmp, 0x557cf400, "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", Both),
				Codec(ImageFormat.Jpeg, 0x557cf401, "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", Both),
				Codec(ImageFormat.Gif, 0x557cf402, "GIF", "*.GIF", "image/gif", Both),
				Codec(ImageFormat.Tiff, 0x557cf405, "TIFF", "*.TIF;*.TIFF", "image/tiff", Both),
				Codec(ImageFormat.Png, 0x557cf406, "PNG", "*.PNG", "image/png", Both),
				Codec(ImageFormat.Icon, 0x557cf407, "ICO", "*.ICO", "image/x-icon", ImageCodecFlags.Decoder),
			};
		}

		public static ImageCodecInfo[] GetImageEncoders()
		{
			return new ImageCodecInfo[] {
				Codec(ImageFormat.Bmp, 0x557cf400, "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", Both),
				Codec(ImageFormat.Jpeg, 0x557cf401, "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", Both),
				Codec(ImageFormat.Gif, 0x557cf402, "GIF", "*.GIF", "image/gif", Both),
				Codec(ImageFormat.Tiff, 0x557cf405, "TIFF", "*.TIF;*.TIFF", "image/tiff", Both),
				Codec(ImageFormat.Png, 0x557cf406, "PNG", "*.PNG", "image/png", Both),
			};
		}
	}
}
