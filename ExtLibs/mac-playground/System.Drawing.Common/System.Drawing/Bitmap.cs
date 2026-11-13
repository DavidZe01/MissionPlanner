//
// System.Drawing.Bitmap.cs
//
// Copyright (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
// Copyright 2011-2013 Xamarin Inc.
//
// Authors: 
//	Alexandre Pigolkine (pigolkine@gmx.de)
//	Christian Meyer (Christian.Meyer@cs.tum.edu)
//	Miguel de Icaza (miguel@ximian.com)
//	Jordi Mas i Hernandez (jmas@softcatala.org)
//	Ravindra (rkumar@novell.com)
//	Sebastien Pouliot  <sebastien@xamarin.com>
//	Kenneth J. Pouncey  <kjpou@pt.lu>
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

using System;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Mac;
using CoreGraphics;
using Foundation;
using ImageIO;
using ObjCRuntime;

namespace System.Drawing {
	
	[Serializable]
	[Editor("System.Drawing.Design.BitmapEditor, System.Drawing.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
			"System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
	[System.Runtime.CompilerServices.TypeForwardedFrom("System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
	public sealed class Bitmap : Image {
		internal CGBitmapContext cachedContext;

		// we will default this to one for now until we get some tests for other image types
		internal int frameCount = 1;
		internal int currentFrame = 0;

		internal PixelFormat pixelFormat;
		internal float dpiWidth = 0;
		internal float dpiHeight = 0;
		internal Size imageSize = Size.Empty;
		internal SizeF physicalDimension = SizeF.Empty;
		internal ImageFormat rawFormat;

		// For in-memory bitmaps
		private IntPtr bitmapBlock;
		private CGDataProvider dataProvider;
		// For images created from PNG, JPEG or other data
		private CGImageSource imageSource;
		private bool dirty;

		public Bitmap (string filename)
			: this (filename, useIcm: false)
		{
		}

		public Bitmap (string filename, bool useIcm)
		{
			// TODO: Implement useIcm

			if (filename == null)
				throw new ArgumentNullException ("Value can not be null");

			try 
			{
				imageSource = CGImageSource.FromUrl(NSUrl.FromFilename(filename));
				if (imageSource == null)
					throw new FileNotFoundException ("File {0} not found.", filename);

				InitializeImageFrame (0);
#if DEBUG
				Tag = filename;
#endif
			}
			catch (Exception) 
			{
				throw new FileNotFoundException ("File {0} not found.", filename);
			}
		}

		public Bitmap (Stream stream)
			: this (stream, false)
		{
		}

		public Bitmap(Stream stream, bool useIcm)
		{
			if (stream == null)
				throw new ArgumentNullException("Value can not be null");

			imageSource = CGImageSource.FromData(NSData.FromStream(stream));
			InitializeImageFrame(0);
		}

		public Bitmap (Type type, string resource)
		{
			if (resource == null)
				throw new ArgumentException ("resource");

			Stream s = type.Assembly.GetManifestResourceStream (type, resource);
			if (s == null) {
				string msg = Locale.GetText ("Resource '{0}' was not found.", resource);
				throw new FileNotFoundException (msg);
			}

			imageSource = CGImageSource.FromData(NSData.FromStream(s));
			InitializeImageFrame(0);
		}

		public Bitmap (int width, int height) : 
			this (width, height, PixelFormat.Format32bppArgb)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="System.Drawing.Bitmap"/> class from the specified existing image..
		/// </summary>
		/// <param name="image">Image.</param>
		public Bitmap (Image image) :
			this (image, image.Width, image.Height)
		{
		}

		public Bitmap (Image original, int width, int height) : 
			this (width, height, PixelFormat.Format32bppArgb)
		{
			using (Graphics graphics = Graphics.FromImage (this)) {
				graphics.DrawImage (original, 0, 0, width, height);
			}
		}

		public Bitmap (Image original, Size newSize) : 
			this (newSize.Width, newSize.Height, PixelFormat.Format32bppArgb)
		{
			using (Graphics graphics = Graphics.FromImage (this)) {
				graphics.DrawImage (original, 0, 0, newSize.Width, newSize.Height);
			}
			DebugSetTag($"Bitmap({original.Tag})");
		}

		public Bitmap (int width, int height, Graphics g) :
			this (width, height)
		{
		}

		public Bitmap (int width, int height, PixelFormat format)
		{
			imageTransform = new CGAffineTransform(1, 0, 0, -1, 0, height);

			int bitsPerComponent, bytesPerRow;
			CGColorSpace colorSpace;
			CGBitmapFlags bitmapInfo;
			bool premultiplied = false;
			int bitsPerPixel = 0;

			pixelFormat = format;

			// Don't forget to set the Image width and height for size.
			imageSize.Width = width;
			imageSize.Height = height;

			switch (format){
			case PixelFormat.Format32bppPArgb:
			case PixelFormat.DontCare:
				premultiplied = true;
				colorSpace = CGColorSpace.CreateDeviceRGB ();
				bitsPerComponent = 8;
				bitsPerPixel = 32;
				bitmapInfo = CGBitmapFlags.PremultipliedLast;
				break;
			case PixelFormat.Format32bppArgb:
				colorSpace = CGColorSpace.CreateDeviceRGB ();
				bitsPerComponent = 8;
				bitsPerPixel = 32;
				bitmapInfo = CGBitmapFlags.PremultipliedLast;
				break;
			case PixelFormat.Format32bppRgb:
				colorSpace = CGColorSpace.CreateDeviceRGB ();
				bitsPerComponent = 8;
				bitsPerPixel = 32;
				bitmapInfo = CGBitmapFlags.NoneSkipLast;
				break;
			case PixelFormat.Format24bppRgb:
				colorSpace = CGColorSpace.CreateDeviceRGB ();
				bitsPerComponent = 8;
				bitsPerPixel = 24;
				bitmapInfo = CGBitmapFlags.None;
				break;
			case PixelFormat.Format8bppIndexed:
				// FIXME: Default palette
				colorSpace = CGColorSpace.CreateIndexed (CGColorSpace.CreateDeviceRGB (), 255, new byte[3 * 256]);
				bitsPerComponent = 8;
				bitsPerPixel = 8;
				bitmapInfo = CGBitmapFlags.None;
				palette = new ColorPalette(0, new Color[256]);
				break;
			case PixelFormat.Format4bppIndexed:
				// FIXME: Default palette
				colorSpace = CGColorSpace.CreateIndexed (CGColorSpace.CreateDeviceRGB (), 15, new byte[3 * 16]);
				bitsPerComponent = 4;
				bitsPerPixel = 4;
				bitmapInfo = CGBitmapFlags.None;
				palette = new ColorPalette(0, new Color[16]);
				break;
			case PixelFormat.Format1bppIndexed:
				// FIXME: Default palette
				colorSpace = CGColorSpace.CreateIndexed (CGColorSpace.CreateDeviceRGB (), 1, new byte[3 * 2]);
				bitsPerComponent = 1;
				bitsPerPixel = 1;
				bitmapInfo = CGBitmapFlags.None;
				palette = new ColorPalette(0, new Color[2] { Color.Black, Color.White });
				break;
			default:
				throw new Exception ("Format not supported: " + format);
			}

			bytesPerRow = (int)(((long)width * bitsPerPixel + 7) / 8);
			int size = bytesPerRow * height;

			bitmapBlock = Marshal.AllocHGlobal (size);
			unsafe {
				byte* b = (byte*)bitmapBlock;
				for (int i = 0; i < size; i++)
					b [i] = 0;
			}
			dataProvider = new CGDataProvider (bitmapBlock, size, true);
			NativeCGImage = NewCGImage (width, height, bitsPerComponent, bitsPerPixel, bytesPerRow, colorSpace, bitmapInfo, dataProvider, null, false, CGColorRenderingIntent.Default);

			dpiWidth = dpiHeight = ConversionHelpers.MS_DPI;
			physicalDimension.Width = width;
			physicalDimension.Height = height;


			// The physical size may be off on certain implementations.  For instance the dpiWidth and dpiHeight 
			// are read using integers in core graphics but in windows it is a float.
			// For example:
			// coregraphics dpiWidth = 24 as integer
			// windows dpiWidth = 24.999935 as float
			// this gives a few pixels difference when calculating the physical size.
			// 256 * 96 / 24 = 1024
			// 256 * 96 / 24.999935 = 983.04
			//
			// https://bugzilla.xamarin.com/show_bug.cgi?id=14365
			// PR: https://github.com/mono/maccore/pull/57
			//

			physicalSize = new SizeF (physicalDimension.Width, physicalDimension.Height);
			physicalSize.Width *= ConversionHelpers.MS_DPI / dpiWidth;
			physicalSize.Height *= ConversionHelpers.MS_DPI / dpiHeight;

			rawFormat = ImageFormat.MemoryBmp;
			pixelFormat = format;

			if (premultiplied) { } // make compiler happy
		}

		// Pixels in GDI+'s layout (B, G, R[, A] in memory) at scan0. GDI+ draws from that memory
		// in place; this takes a copy in the internal layout (premultiplied RGBA), since the
		// owner of scan0 (a video frame, say) may free or reuse it right after.
		public Bitmap (int width, int height, int stride, PixelFormat format, IntPtr scan0)
			: this (width, height, format)
		{
			if (scan0 == IntPtr.Zero)
				return;
			var bitsPerPixel = GetBitsPerPixel (format);
			var bytesPerRow = (int)NativeCGImage.BytesPerRow;
			var rowBytes = Math.Min (Math.Abs (stride), bytesPerRow);
			unsafe {
				for (int y = 0; y < height; y++)
					Buffer.MemoryCopy ((byte*)scan0 + (long)y * stride, (byte*)bitmapBlock + (long)y * bytesPerRow, bytesPerRow, rowBytes);

				byte* row = (byte*)bitmapBlock;
				if (bitsPerPixel == 32 && format == PixelFormat.Format32bppArgb) {
					if (!ConversionHelpers.sTablesInitialized)
						ConversionHelpers.CalculateTables ();
					Convert_BGRA_8888_To_P_RGBA_8888 (bitmapBlock, width, height, bytesPerRow);
				} else if (bitsPerPixel == 32 || bitsPerPixel == 24) {
					// premultiplied or without alpha: only red and blue change places
					var pixelBytes = bitsPerPixel / 8;
					for (int y = 0; y < height; y++, row += bytesPerRow)
						for (int x = 0; x < width * pixelBytes; x += pixelBytes) {
							var blue = row [x];
							row [x] = row [x + 2];
							row [x + 2] = blue;
							if (format == PixelFormat.Format32bppRgb)
								row [x + 3] = 255;
						}
				}
			}
			dirty = true;
		}

		private Bitmap (SerializationInfo info, StreamingContext context)
		{
			foreach (SerializationEntry serEnum in info) {
				if (String.Compare(serEnum.Name, "Data", true) == 0) {
					byte[] bytes = (byte[]) serEnum.Value;
					if (bytes != null && bytes.Length > 0)
					{
						imageSource = CGImageSource.FromData(NSData.FromArray(bytes));
						InitializeImageFrame(0);
					}
				}
			}
		}

		internal Bitmap (CGImage image)
		{
			imageTransform = new CGAffineTransform(1, 0, 0, -1, 0, (float)image.Height);
			InitWithCGImage(image);
			GuessPixelFormat();
		}

		public IntPtr GetHbitmap()
		{
			throw new PlatformNotSupportedException ();
		}

		public IntPtr GetHbitmap(Color backgroundColor)
		{
			throw new PlatformNotSupportedException ();
		}

		public IntPtr GetHicon()
		{
			throw new PlatformNotSupportedException ();
		}

		public Bitmap FromHicon(IntPtr handle)
		{
			throw new PlatformNotSupportedException ();
		}


		public static Bitmap FromResource(IntPtr hinstance, string bitmapName)
		{
			throw new PlatformNotSupportedException ();
		}

		private void InitializeImageFrame(int frame)
		{
			if (NativeCGImage != null)
				NativeCGImage.Dispose();

			SetImageInformation (frame);
			imageTransform = new CGAffineTransform(1, 0, 0, -1, 0, physicalDimension.Height);
			InitWithCGImage (imageSource.CreateImage(frame, null));
			GuessPixelFormat ();

			currentFrame = frame;
		}

		// animated images (GIF): the frame shown and drawn, as Image.SelectActiveFrame chooses it
		internal void SelectFrame(int frame)
		{
			if (imageSource != null && frame != currentFrame)
				InitializeImageFrame(frame);
		}

		// each frame's delay in hundredths of a second (GDI+'s PropertyTagFrameDelay, 0x5100)
		internal int[] FrameDelays()
		{
			var delays = new int[Math.Max(1, frameCount)];
			if (imageSource == null)
				return delays;
			for (int i = 0; i < frameCount; i++)
			{
				using (var properties = imageSource.CopyProperties((NSDictionary)null, i))
				{
					var gif = properties?[new NSString("{GIF}")] as NSDictionary;
					var delay = (gif?[new NSString("UnclampedDelayTime")] ?? gif?[new NSString("DelayTime")]) as NSNumber;
					if (delay != null)
						delays[i] = (int)Math.Round(delay.DoubleValue * 100);
				}
			}
			return delays;
		}

		private void GuessPixelFormat()
		{
			bool hasAlpha;
			CGColorSpace colorSpace;
			int bitsPerComponent;
			bool premultiplied = false;
			int bitsPerPixel = 0;
			CGImageAlphaInfo alphaInfo;

			var image = NativeCGImage;

			if (image == null) {
				throw new ArgumentException (" image is invalid! " );
			}

			alphaInfo = image.AlphaInfo;
			hasAlpha = ((alphaInfo == CGImageAlphaInfo.PremultipliedLast) || (alphaInfo == CGImageAlphaInfo.PremultipliedFirst) || (alphaInfo == CGImageAlphaInfo.Last) || (alphaInfo == CGImageAlphaInfo.First) ? true : false);

			imageSize.Width = (int)image.Width;
			imageSize.Height = (int)image.Height;

			// Not sure yet if we need to keep the original image information
			// before we change it internally.  TODO look at what windows does
			// and follow that.
			bitsPerComponent = (int)image.BitsPerComponent;
			bitsPerPixel = (int)image.BitsPerPixel;

			colorSpace = image.ColorSpace;

			if (colorSpace != null)
			{
				if (colorSpace.Model == CGColorSpaceModel.RGB) {
					if (bitsPerPixel == 32) {
						if (hasAlpha) {
							if (alphaInfo == CGImageAlphaInfo.PremultipliedFirst) 
							{
								premultiplied = true;
								pixelFormat = PixelFormat.Format32bppPArgb;
							}

							if (alphaInfo == CGImageAlphaInfo.First)
								pixelFormat = PixelFormat.Format32bppArgb;

							if (alphaInfo == CGImageAlphaInfo.Last)
								pixelFormat = PixelFormat.Format32bppRgb;

							if (alphaInfo == CGImageAlphaInfo.PremultipliedLast) 
							{
								premultiplied = true;
								pixelFormat = PixelFormat.Format32bppRgb;
							}


						} else {
							pixelFormat = PixelFormat.Format24bppRgb;
						}
					} else {
						// Right now microsoft looks like it is using Format32bppRGB for other
						// need more test cases to verify
						pixelFormat = PixelFormat.Format32bppArgb;
					}
				} else {
					// Right now microsoft looks like it is using Format32bppRGB for other
					// MonoChrome is set to 32bpppArgb
					// need more test cases to verify
					pixelFormat = PixelFormat.Format32bppArgb;
				}

			}
			else
			{
				// need more test cases to verify
				pixelFormat = PixelFormat.Format32bppArgb;

			}

			if (premultiplied) { // make compiler happy
			}
		}


		private void SetImageInformation(int frame)
		{
			frameCount = (int)imageSource.ImageCount;
			if (frameCount == 0)
				throw new ArgumentException("Invalid image");

			var properties = imageSource.GetProperties (frame, null);

			// This needs to be incorporated in frame information later
			// as well as during the clone methods.
			dpiWidth =  properties.DPIWidthF != null ? (float)properties.DPIWidthF : ConversionHelpers.MS_DPI;
			dpiHeight = properties.DPIWidthF != null ? (float)properties.DPIHeightF : ConversionHelpers.MS_DPI;
			physicalDimension.Width = (float)properties.PixelWidth;
			physicalDimension.Height = (float)properties.PixelHeight;


			// The physical size may be off on certain implementations.  For instance the dpiWidth and dpiHeight 
			// are read using integers in core graphics but in windows it is a float.
			// For example:
			// coregraphics dpiWidth = 24 as integer
			// windows dpiWidth = 24.999935 as float
			// this gives a few pixels difference when calculating the physical size.
			// 256 * 96 / 24 = 1024
			// 256 * 96 / 24.999935 = 983.04
			//
			// https://bugzilla.xamarin.com/show_bug.cgi?id=14365
			// PR: https://github.com/mono/maccore/pull/57
			//

			physicalSize = new SizeF (physicalDimension.Width, physicalDimension.Height);
			physicalSize.Width *= ConversionHelpers.MS_DPI / dpiWidth;
			physicalSize.Height *= ConversionHelpers.MS_DPI / dpiHeight;

			// Set the raw image format
			// We will use the UTI from the image source
			rawFormat = ImageFormatFromUTI(imageSource.TypeIdentifier);
		}

		internal static ImageFormat ImageFormatFromUTI(string uti)
		{
			switch (uti)
			{
				case "public.png":return ImageFormat.Png;
				case "com.microsoft.bmp": return ImageFormat.Bmp;
				case "com.compuserve.gif": return ImageFormat.Gif;
				case "public.jpeg": return ImageFormat.Jpeg;
				case "public.tiff": return ImageFormat.Tiff;
				case "com.microsoft.ico": return ImageFormat.Icon;
				case "com.adobe.pdf": return ImageFormat.Wmf;
				default: return ImageFormat.Png;
			}
		}

		private void InitWithCGImage (CGImage image)
		{
			int	width, height;
			CGBitmapContext bitmap = null;
			bool hasAlpha;
			CGImageAlphaInfo alphaInfo;
			CGColorSpace colorSpace;
			int bitsPerComponent, bytesPerRow;
			CGBitmapFlags bitmapInfo;
			int bitsPerPixel = 0;

			if (image == null || image.BitsPerComponent == 0) {
				throw new ArgumentException (" image is invalid! " );
			}

			alphaInfo = image.AlphaInfo;
			hasAlpha = ((alphaInfo == CGImageAlphaInfo.PremultipliedLast) || (alphaInfo == CGImageAlphaInfo.PremultipliedFirst) || (alphaInfo == CGImageAlphaInfo.Last) || (alphaInfo == CGImageAlphaInfo.First) ? true : false);
			
			imageSize.Width = (int)image.Width;
			imageSize.Height = (int)image.Height;
			
			width = (int)image.Width;
			height = (int)image.Height;

			// Not sure yet if we need to keep the original image information
			// before we change it internally.  TODO look at what windows does
			// and follow that.
			bitmapInfo = image.BitmapInfo;
			bitsPerComponent = (int)image.BitsPerComponent;
			bitsPerPixel = (int)image.BitsPerPixel;
			bytesPerRow = width * bitsPerPixel/bitsPerComponent;
			int size = bytesPerRow * height;
			
			colorSpace = image.ColorSpace;

			// Right now internally we represent the images all the same
			// I left the call here just in case we find that this is not
			// possible.  Read the comments for non alpha images.
			if(colorSpace != null) {
				if( hasAlpha ) {
					colorSpace = CGColorSpace.CreateDeviceRGB ();
					bitsPerComponent = 8;
					bitsPerPixel = 32;
					bitmapInfo = CGBitmapFlags.PremultipliedLast;
				}
				else
				{
					// even for images without alpha we will internally 
					// represent them as RGB with alpha.  There were problems
					// if we do not do it this way and creating a bitmap context.
					// The images were not drawing correctly and tearing.  Also
					// creating a Graphics to draw on was a nightmare.  This
					// should probably be looked into or maybe it is ok and we
					// can continue representing internally with this representation
					colorSpace = CGColorSpace.CreateDeviceRGB ();
					bitsPerComponent = 8;
					bitsPerPixel = 32;
					bitmapInfo = CGBitmapFlags.NoneSkipLast;
				}
			} else {
				colorSpace = CGColorSpace.CreateDeviceRGB ();
				bitsPerComponent = 8;
				bitsPerPixel = 32;
				bitmapInfo = CGBitmapFlags.NoneSkipLast;
			}

			bytesPerRow = width * bitsPerPixel/bitsPerComponent;
			size = bytesPerRow * height;

			var bitmapBlock = Marshal.AllocHGlobal(size);
			bitmap = new CGBitmapContext (bitmapBlock, 
										  width, height, 
										  bitsPerComponent, 
										  bytesPerRow,
										  colorSpace,
										  bitmapInfo);

			bitmap.ClearRect (new CGRect (0, 0, width, height));
			bitmap.DrawImage (new CGRect (0, 0, image.Width, image.Height), image);

			this.bitmapBlock = bitmapBlock;
			this.dataProvider = new CGDataProvider (bitmapBlock, size, true);
			NativeCGImage = NewCGImage (width, height, bitsPerComponent, 
										 bitsPerPixel, bytesPerRow, 
										 colorSpace,
										 bitmapInfo, dataProvider, null, true, image.RenderingIntent);

			colorSpace.Dispose();
			cachedContext = bitmap;
		}

		// Graphics made by Graphics.FromImage that are not disposed yet: they draw into the
		// bitmap's context at any time (Mission Planner's HUD keeps one for its back buffer),
		// so the image is taken from the context on every use while there are any.
		internal int liveGraphics;

		internal void MarkDirty ()
		{
			dirty = true;
		}

		// A new context on the bitmap's memory (that of GetRenderableContext), with a state of
		// its own: for Graphics.FromImage. null when the bitmap has no such memory.
		internal CGBitmapContext NewDrawingContext ()
		{
			var shared = GetRenderableContext ();
			var data = shared?.Data ?? IntPtr.Zero;
			if (data == IntPtr.Zero)
				return null;
			try {
				return new CGBitmapContext (data, shared.Width, shared.Height, shared.BitsPerComponent,
					shared.BytesPerRow, shared.ColorSpace, shared.BitmapInfo);
			}
			catch (Exception) {
				return null;
			}
		}

		internal override CGImage NativeCGImage {
			get {
				if (dirty || liveGraphics > 0) {
					dirty = false;
					var context = GetRenderableContext();
					base.NativeCGImage = Snapshot(context) ?? context.ToImage();
				}
				return base.NativeCGImage;
			}
		}

		// An image of the bitmap's memory as it is now. The drawing may have gone through
		// another context on the same memory (Graphics.FromImage), so do not rely on what
		// the bitmap's own context knows.
		static CGImage Snapshot (CGBitmapContext context)
		{
			var data = context?.Data ?? IntPtr.Zero;
			if (data == IntPtr.Zero)
				return null;
			try {
				using var bytes = NSData.FromBytes (data, (nuint)(context.BytesPerRow * context.Height));
				using var provider = new CGDataProvider (bytes);
				return new CGImage ((int)context.Width, (int)context.Height, (int)context.BitsPerComponent,
					(int)context.BitsPerPixel, (int)context.BytesPerRow, context.ColorSpace, context.BitmapInfo,
					provider, null, true, CGColorRenderingIntent.Default);
			}
			catch (Exception) {
				return null;
			}
		}

		CGImage NewCGImage(int width, int height, int bitsPerComponent, int bitsPerPixel, int bytesPerRow, CGColorSpace colorSpace, CGBitmapFlags bitmapFlags, CGDataProvider provider, nfloat[] decode, bool shouldInterpolate, CGColorRenderingIntent intent)
		{
			try
			{
				return new CGImage(width, height, bitsPerComponent, bitsPerPixel, bytesPerRow, colorSpace, bitmapFlags, dataProvider, decode, shouldInterpolate, intent);
			}
			catch (Exception inner)
			{
				var e = new ArgumentException("Failed to create CGImage", inner);
				e.Data["width"] = width;
				e.Data["height"] = height;
				e.Data["bitsPerComponent"] = bitsPerComponent;
				e.Data["bitsPerPixel"] = bitsPerPixel;
				e.Data["bytesPerRow"] = bytesPerRow;
				e.Data["colorSpace"] = colorSpace;
				e.Data["bitmapFlags"] = bitmapFlags;
				e.Data["dataProvider"] = dataProvider;
				e.Data["decode"] = decode;
				e.Data["shouldInterpolate"] = shouldInterpolate;
				e.Data["intent"] = intent;
				throw e;
			}
		}

		internal CGBitmapContext GetRenderableContext()
		{
			if (cachedContext != null && cachedContext.Handle != IntPtr.Zero)
				return cachedContext;

			if (bitmapBlock != IntPtr.Zero && (PixelFormat & PixelFormat.Indexed) == 0 && this.NativeCGImage.BitsPerPixel == 32) {
				try {
					CGImage i = this.NativeCGImage;
					cachedContext = new CGBitmapContext(bitmapBlock, i.Width,i.Height, i.BitsPerComponent, i.BytesPerRow, i.ColorSpace, i.BitmapInfo);
				}
				catch (Exception) {
					InitWithCGImage (NativeCGImage);
					GuessPixelFormat ();	
				}
			} else {
				InitWithCGImage (NativeCGImage);
				GuessPixelFormat ();	
			}

			return cachedContext;
		}

		internal new void RotateFlip (RotateFlipType rotateFlipType)
		{
			CGAffineTransform rotateFlip = CGAffineTransform.MakeIdentity();

			int width, height;
			width = (int)NativeCGImage.Width;
			height = (int)NativeCGImage.Height;

			switch (rotateFlipType) 
			{
				//			case RotateFlipType.RotateNoneFlipNone:
				//			//case RotateFlipType.Rotate180FlipXY:
				//				rotateFlip = GeomUtilities.CreateRotateFlipTransform (b.Width, b.Height, 0, false, false);
				//				break;
				case RotateFlipType.Rotate90FlipNone:
				//case RotateFlipType.Rotate270FlipXY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 90, false, false);
				break;
				case RotateFlipType.Rotate180FlipNone:
				//case RotateFlipType.RotateNoneFlipXY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 0, true, true);
				break;
				case RotateFlipType.Rotate270FlipNone:
				//case RotateFlipType.Rotate90FlipXY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 270, false, false);
				break;
				case RotateFlipType.RotateNoneFlipX:
				//case RotateFlipType.Rotate180FlipY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 0, true, false);
				break;
				case RotateFlipType.Rotate90FlipX:
				//case RotateFlipType.Rotate270FlipY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 90, true, false);
				break;
				case RotateFlipType.Rotate180FlipX:
				//case RotateFlipType.RotateNoneFlipY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 0, false, true);
				break;
				case RotateFlipType.Rotate270FlipX:
				//case RotateFlipType.Rotate90FlipY:
				rotateFlip = GeomUtilities.CreateRotateFlipTransform (ref width, ref height, 270, true, false);
				break;
			}

			var img = NativeCGImage;
			var bytesPerRow = (width * (int)img.BitsPerPixel + 7) / 8;
			var newBitmapBlock = Marshal.AllocHGlobal(height * bytesPerRow);
			var newBitmapContext = new CGBitmapContext(newBitmapBlock, width, height, img.BitsPerComponent, bytesPerRow, img.ColorSpace, img.AlphaInfo);
			newBitmapContext.ConcatCTM(rotateFlip);
			newBitmapContext.DrawImage(new CGRect(0, 0, img.Width, img.Height), img);
			newBitmapContext.Flush();

			// If the width or height is not the seme we need to switch the dpiHeight and dpiWidth
			// We should be able to get around this with set resolution later.
			if (img.Width != width || img.Height != height)
			{
				var temp = dpiWidth;
				dpiHeight = dpiWidth;
				dpiWidth = temp;
			}

			physicalDimension.Width = (float)width;
			physicalDimension.Height = (float)height;

			physicalSize = new SizeF (physicalDimension.Width, physicalDimension.Height);
			physicalSize.Width *= ConversionHelpers.MS_DPI / dpiWidth;
			physicalSize.Height *= ConversionHelpers.MS_DPI / dpiHeight;

			// In windows the RawFormat is changed to MemoryBmp to show that the image has changed.
			rawFormat = ImageFormat.MemoryBmp;

			// Set our transform for this image for the new height
			imageTransform = new CGAffineTransform(1, 0, 0, -1, 0, height);

			// bitmapBlock is owned by dataProvider and freed implicitly
			if (dataProvider != null)
				dataProvider.Dispose();

			if (cachedContext != null)
				cachedContext.Dispose();
			NativeCGImage.Dispose();

			this.bitmapBlock = newBitmapBlock;
			this.dataProvider = new CGDataProvider(bitmapBlock, height * bytesPerRow);
			this.NativeCGImage = newBitmapContext.ToImage();
			this.cachedContext = newBitmapContext;
			this.imageSource = null;

			// update the cached size
			imageSize.Width = this.Width;
			imageSize.Height = this.Height;
		}

		/// <summary>
		/// Creates a copy of the section of this Bitmap defined by Rectangle structure and with a specified PixelFormat enumeration.
		/// </summary>
		/// <param name="rect">Rect.</param>
		/// <param name="pixelFormat">Pixel format.</param>
		public Bitmap Clone (Rectangle rect, PixelFormat pixelFormat)
		{
			return Clone ((RectangleF) rect, pixelFormat);
		}

		/// <summary>
		/// Creates a copy of the section of this Bitmap defined by Rectangle structure and with a specified PixelFormat enumeration.
		/// </summary>
		/// <param name="rect">Rect.</param>
		/// <param name="pixelFormat">Pixel format.</param>
		public Bitmap Clone (RectangleF rect, PixelFormat pixelFormat)
		{
			if ((int)rect.Width == 0 || (int)rect.Height == 0)
				throw new ArgumentException ("Width or Height of rect is 0.");

			var width = (int)rect.Width;
			var height = (int)rect.Height;

			var tmpImg = new Bitmap (width, height, pixelFormat);

			using (Graphics g = Graphics.FromImage (tmpImg)) {
				g.DrawImage (this, new Rectangle(0, 0, width, height), rect, GraphicsUnit.Pixel );
			}
			tmpImg.DebugSetTag($"Bitmap.Clone({Tag})");
			return tmpImg;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing) {
				if (dataProvider != null) {
					dataProvider.Dispose ();
					dataProvider = null;
				}
				if (imageSource != null)
				{
					imageSource.Dispose();
					imageSource = null;
				}
			}
			base.Dispose (disposing);
		}
		
		public Color GetPixel (int x, int y)
		{
			if (x < 0 || x > NativeCGImage.Width - 1)
				throw new InvalidEnumArgumentException ("Parameter must be positive and < Width.");
			if (y < 0 || y > NativeCGImage.Height - 1)
				throw new InvalidEnumArgumentException ("Parameter must be positive and < Height.");

			// We are going to cheat here and instead of reading the bytes of the original image
			// parsing from there a pixel and converting to a format we will just create 
			// a 1 x 1 image of the pixel that we want.  I am supposing this should be really
			// fast.
			var pixelImage = NativeCGImage.WithImageInRect(new CGRect(x,y,1,1));

			var pData = pixelImage.DataProvider;
			var nData = pData.CopyData ();

			// We may have to parse out the bytes with 4 or 3 bytes per pixel later.
			var pixelColor = Color.FromArgb(nData[3], nData[0], nData[1], nData[2]);
			pixelImage.Dispose ();

			return pixelColor;
		}

		public void SetPixel (int x, int y, Color color)
		{
			if (x < 0 || x > NativeCGImage.Width - 1)
				throw new InvalidEnumArgumentException ("Parameter must be positive and < Width.");
			if (y < 0 || y > NativeCGImage.Height - 1)
				throw new InvalidEnumArgumentException ("Parameter must be positive and < Height.");

			dirty = true;
			GetRenderableContext ();

			// We are going to cheat here by drawing directly to the cached context that is 
			// associated to the image.  This way we do not have to play with pixels and offsets
			// to change the data.  If this proves to be non performant then we will change it later.
			cachedContext.SaveState ();
			cachedContext.ConcatCTM (cachedContext.GetCTM ().Invert ());
			cachedContext.ConcatCTM (imageTransform);
			cachedContext.SetFillColor(color);
			cachedContext.FillRect (new CGRect(x,y, 1,1));
			cachedContext.FillPath ();
			cachedContext.RestoreState();
		}


		public void SetResolution (float xDpi, float yDpi)
		{
			//throw new NotImplementedException ();
		}

		public void MakeTransparent() 
		{
			// Todo: Instead of passing white here we need to read
			// the Lower-Left pixel of image.  Found this by
			// placing a color in each corner until MakeTransparent 
			// did not work.
			//      bitmap.SetPixel(0, bitmap.Height - 1, Color.Magenta);ß

			MakeTransparent (GetPixel(0, Height - 1));
		}

		public void MakeTransparent(Color transparentColor)
		{
			// FIXME: Make sure we have an alpha channel
			//MakeSureWeHaveAnAlphaChannel ();

			var colorValues = transparentColor.ElementsRGBA ();

			// Lock the bitmap's bits.  
			Rectangle rect = new Rectangle(0, 0, Width, Height);
			var bmpData = LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
							 pixelFormat);

			var alpha = Color.Transparent.A;
			var red = Color.Transparent.R;
			var green = Color.Transparent.G;
			var blue = Color.Transparent.B;

			byte alphar = Color.Transparent.A;
			byte redr = Color.Transparent.R;
			byte greenr = Color.Transparent.G;
			byte bluer = Color.Transparent.B;
		
			bool match = false;

			var pixelSize = GetBitsPerPixel (pixelFormat) / 8;
			unsafe 
			{
				for (int y=0; y<Height; y++) {
					byte* row = (byte*)bmpData.Scan0 + (y * bmpData.Stride);
					for (int x=0; x<bmpData.Stride; x=x+pixelSize) {

						redr = row [x + 2];;
						greenr = row [x + 1];
						bluer = row [x];
						alphar = row [x + 3];

						match = false;

						if (row [x + 2] == colorValues [0] && row [x + 1] == colorValues [1]
							&& row [x] == colorValues [2] && row [x + 3] == colorValues [3]) 
							match = true;


						if (match) 
						{
							// we process bgra
							row [x] = blue;
							row [x + 1] = green;
							row [x + 2] = red;

							if (pixelSize == 4)
								row [x + 3] = alpha;
						}
					}

				}
			}

			// Unlock the bits.
			UnlockBits(bmpData);
		}

		// GDI+ has encoders for BMP, JPEG, GIF, TIFF and PNG; any other format (MemoryBmp, Emf,
		// Wmf, Exif...) is written as PNG, as Windows does
		private string GetTypeIdentifier(ImageFormat format)
		{
			var guid = format?.Guid ?? ImageFormat.Png.Guid;
			if (guid == ImageFormat.Bmp.Guid)
				return "com.microsoft.bmp";
			if (guid == ImageFormat.Gif.Guid)
				return "com.compuserve.gif";
			if (guid == ImageFormat.Jpeg.Guid)
				return "public.jpeg";
			if (guid == ImageFormat.Tiff.Guid)
				return "public.tiff";
			return "public.png";
		}

		// JPEG quality: Encoder.Quality (0-100) if given, else GDI+'s default of 75
		private static CGImageDestinationOptions GetDestinationOptions(ImageFormat format, EncoderParameters parameters)
		{
			if (format?.Guid != ImageFormat.Jpeg.Guid)
				return null;
			long quality = 75;
			if (parameters?.Param != null)
				foreach (var p in parameters.Param)
					if (p?.Encoder?.Guid == Encoder.Quality.Guid && p.FirstLong() is long q)
						quality = Math.Max(0, Math.Min(100, q));
			return new CGImageDestinationOptions { LossyCompressionQuality = quality / 100f };
		}

		private void Save(CGImageDestination dest, CGImageDestinationOptions options = null)
		{
			if (NativeCGImage == null)
				throw new ObjectDisposedException("cgimage");

			bool lastOK = true;
			int savedFrame = currentFrame, framesSaved = 0;
			for (int frame = 0; frame < frameCount; frame++)
				if (lastOK = TryAddFrame(dest, frame, options))
					++framesSaved;

			if (currentFrame != savedFrame || !lastOK)
				InitializeImageFrame(savedFrame);

			if (framesSaved == 0)
				throw new ArgumentException("No frame could be saved");

			dest.Close();
		}

		bool TryAddFrame(CGImageDestination dest, int frame, CGImageDestinationOptions options)
		{
			try
			{
				var corrupted = imageSource != null && imageSource.GetPropertiesSafe(frame) == null;
				if (frame != currentFrame && imageSource != null && !corrupted)
					InitializeImageFrame(frame);

				if (!corrupted)
					dest.AddImage(NativeCGImage, options);

				return true;
			}
			catch (Exception e)
			{
				Debug.Assert(false, "Failed adding destination: " + e.ToString());
				return false;
			}
		}

		public new void Save(string path, ImageCodecInfo encoder, EncoderParameters parameters)
		{
			if (encoder == null)
				throw new ArgumentNullException(nameof(encoder));
			Save(path, encoder.Format, parameters);
		}

		public new void Save (string path, ImageFormat format)
		{
			Save(path, format, null);
		}

		void Save(string path, ImageFormat format, EncoderParameters parameters)
		{
			if (path == null)
				throw new ArgumentNullException ("path");

			// Obtain a URL file path to be passed
			NSUrl url = NSUrl.FromFilename(path);

			// Create an image destination that saves into the path that is passed in
			using (var dest = CGImageDestination.Create(url, GetTypeIdentifier(format), frameCount))
				Save(dest, GetDestinationOptions(format, parameters));
		}

		// as on Windows, in the image's own format whatever the file's extension (a bitmap made
		// in memory, MemoryBmp, is written as PNG)
		public new void Save (string path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Save (path, RawFormat);
		}

		public new void Save(Stream stream, ImageCodecInfo encoder, EncoderParameters parameters)
		{
			if (encoder == null)
				throw new ArgumentNullException(nameof(encoder));
			Save(stream, encoder.Format, parameters);
		}

		public new void Save(Stream stream, ImageFormat format)
		{
			Save(stream, format, null);
		}

		void Save(Stream stream, ImageFormat format, EncoderParameters parameters)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			
			using (var imageData = new NSMutableData())
			{
				using (var dest = CGImageDestination.Create(imageData, GetTypeIdentifier(format), frameCount))
					Save(dest, GetDestinationOptions(format, parameters));

				using (var ms = imageData.AsStream())
					ms.CopyTo(stream, 64 * 1024);
			}
		}

		public BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat format)
		{
			return LockBits (rect, flags, format, new BitmapData());
		}

		public BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat pixelFormat, BitmapData bitmapData)
		{
			if (rect != new RectangleF (new PointF (0, 0), physicalDimension)) 
				throw new NotImplementedException("Sub rectangles of bitmaps not supported yet.");
			// another format: a converted copy, as GDI+ gives (a JPEG map tile read as 32bpp ARGB)
			if (PixelFormat != pixelFormat)
				return LockBitsConverted (rect, flags, pixelFormat, bitmapData);

			// Bitmap created from external data, convert it
			if (bitmapBlock == IntPtr.Zero)
				GetRenderableContext ();

			//bitmapData.Scan0 = (IntPtr)((long)pinnedScanArray.AddrOfPinnedObject() + ((rect.Left * NativeCGImage.BitsPerPixel + 7) / 8) + (rect.Top * NativeCGImage.BytesPerRow));
			bitmapData.Scan0 = bitmapBlock;
			bitmapData.Height = (int)rect.Height;
			bitmapData.Width = (int)rect.Width;
			bitmapData.PixelFormat = pixelFormat;
			bitmapData.Stride = (int)NativeCGImage.BytesPerRow;
			bitmapData.Reserved = (int)flags;

			if (flags != ImageLockMode.WriteOnly) {
				if (NativeCGImage.BitsPerPixel == 32 && (PixelFormat & PixelFormat.Indexed) == 0) {
					if (!ConversionHelpers.sTablesInitialized)
						ConversionHelpers.CalculateTables ();
					// Reading only (an OpenGL texture, say): GDI+ leaves the bitmap as it is,
					// so convert a copy; converted in place, the bitmap kept GDI+'s layout
					// and showed wrong colours afterwards. UnlockBits frees the copy.
					if (flags == ImageLockMode.ReadOnly) {
						var size = bitmapData.Stride * bitmapData.Height;
						var copy = Marshal.AllocHGlobal (size);
						unsafe {
							Buffer.MemoryCopy ((void*)bitmapBlock, (void*)copy, size, size);
						}
						bitmapData.Scan0 = copy;
					}
					Convert_P_RGBA_8888_To_BGRA_8888 (bitmapData.Scan0, bitmapData.Width, bitmapData.Height, bitmapData.Stride);
				}
			}

			return bitmapData;
		}

		// BitmapData.Reserved of a converted copy (LockBitsConverted)
		const int ConvertedCopy = 0x10000;

		BitmapData LockBitsConverted (Rectangle rect, ImageLockMode flags, PixelFormat format, BitmapData bitmapData)
		{
			if (format != PixelFormat.Format32bppArgb && format != PixelFormat.Format32bppPArgb &&
			    format != PixelFormat.Format32bppRgb && format != PixelFormat.Format24bppRgb)
				throw new ArgumentException ("Conversion to " + format + " is not supported", "pixelFormat");
			int width = rect.Width, height = rect.Height;
			int bytesPerPixel = GetBitsPerPixel (format) / 8;
			int stride = (width * bytesPerPixel + 3) & ~3;
			var buffer = Marshal.AllocHGlobal (stride * height);
			if (flags != ImageLockMode.WriteOnly) {
				// CoreGraphics draws the image as premultiplied BGRA (GDI+'s byte order)
				int bgraStride = width * 4;
				var bgra = bytesPerPixel == 4 ? buffer : Marshal.AllocHGlobal (bgraStride * height);
				try {
					using (var colorSpace = CGColorSpace.CreateDeviceRGB ())
					using (var context = new CGBitmapContext (bgra, width, height, 8, bytesPerPixel == 4 ? stride : bgraStride, colorSpace,
						CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.PremultipliedFirst)) {
						context.ClearRect (new CGRect (0, 0, width, height));
						context.DrawImage (new CGRect (0, 0, width, height), NativeCGImage);
					}
					unsafe {
						for (int y = 0; y < height; y++) {
							byte* src = (byte*)bgra + (long)y * (bytesPerPixel == 4 ? stride : bgraStride);
							byte* dst = (byte*)buffer + (long)y * stride;
							for (int x = 0; x < width; x++, src += 4) {
								byte a = src [3];
								if (format == PixelFormat.Format32bppArgb && a != 0 && a != 255) {
									src [0] = (byte)Math.Min (255, src [0] * 255 / a);
									src [1] = (byte)Math.Min (255, src [1] * 255 / a);
									src [2] = (byte)Math.Min (255, src [2] * 255 / a);
								} else if (format == PixelFormat.Format32bppRgb) {
									src [3] = 255;
								} else if (bytesPerPixel == 3) {
									dst [x * 3] = src [0];
									dst [x * 3 + 1] = src [1];
									dst [x * 3 + 2] = src [2];
								}
							}
						}
					}
				} finally {
					if (bgra != buffer)
						Marshal.FreeHGlobal (bgra);
				}
			}
			bitmapData.Scan0 = buffer;
			bitmapData.Width = width;
			bitmapData.Height = height;
			bitmapData.PixelFormat = format;
			bitmapData.Stride = stride;
			bitmapData.Reserved = (int)flags | ConvertedCopy;
			return bitmapData;
		}

		// what was written into a converted copy goes back into the bitmap
		void UnlockConverted (BitmapData data)
		{
			int width = data.Width, height = data.Height;
			int bytesPerPixel = GetBitsPerPixel (data.PixelFormat) / 8;
			int bgraStride = width * 4;
			var bgra = Marshal.AllocHGlobal (bgraStride * height);
			try {
				unsafe {
					for (int y = 0; y < height; y++) {
						byte* src = (byte*)data.Scan0 + (long)y * data.Stride;
						byte* dst = (byte*)bgra + (long)y * bgraStride;
						for (int x = 0; x < width; x++, src += bytesPerPixel, dst += 4) {
							byte a = bytesPerPixel == 4 && data.PixelFormat != PixelFormat.Format32bppRgb ? src [3] : (byte)255;
							bool premultiply = data.PixelFormat == PixelFormat.Format32bppArgb && a != 255;
							dst [0] = premultiply ? (byte)(src [0] * a / 255) : src [0];
							dst [1] = premultiply ? (byte)(src [1] * a / 255) : src [1];
							dst [2] = premultiply ? (byte)(src [2] * a / 255) : src [2];
							dst [3] = a;
						}
					}
				}
				using (var colorSpace = CGColorSpace.CreateDeviceRGB ())
				using (var provider = new CGDataProvider (bgra, bgraStride * height, false))
				using (var image = new CGImage (width, height, 8, 32, bgraStride, colorSpace,
					CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.PremultipliedFirst, provider, null, false, CGColorRenderingIntent.Default)) {
					var context = GetRenderableContext ();
					context.SaveState ();
					context.ConcatCTM (context.GetCTM ().Invert ());
					context.SetBlendMode (CGBlendMode.Copy);
					context.DrawImage (new CGRect (0, 0, width, height), image);
					context.RestoreState ();
				}
			} finally {
				Marshal.FreeHGlobal (bgra);
			}
			dirty = true;
		}

		public void UnlockBits (BitmapData data)
		{
			if ((data.Reserved & ConvertedCopy) != 0) {
				if ((ImageLockMode)(data.Reserved & ~ConvertedCopy) != ImageLockMode.ReadOnly)
					UnlockConverted (data);
				Marshal.FreeHGlobal (data.Scan0);
				data.Scan0 = IntPtr.Zero;
				return;
			}
			if ((ImageLockMode)data.Reserved == ImageLockMode.ReadOnly) {
				if (data.Scan0 != bitmapBlock && data.Scan0 != IntPtr.Zero) {
					Marshal.FreeHGlobal (data.Scan0);
					data.Scan0 = IntPtr.Zero;
				}
				return;
			}
			
			if (NativeCGImage.BitsPerPixel == 32  && (PixelFormat & PixelFormat.Indexed) == 0) {
				if (!ConversionHelpers.sTablesInitialized)
					ConversionHelpers.CalculateTables ();
				Convert_BGRA_8888_To_P_RGBA_8888 (data.Scan0, data.Width, data.Height, data.Stride);
			}

			// Set the dirty flag *after* accessing the NativeCGImage getter, to avoid losing changes in the bitmap data.
			dirty = true;
		}

		// Our internal format is pre-multiplied alpha
		static unsafe void Convert_P_RGBA_8888_To_BGRA_8888(IntPtr bitmapBlock, int width, int height, int stride)
		{
			byte temp = 0;
			byte alpha = 0;
			byte* buffer = (byte*)bitmapBlock;

			for (int y = 0; y < height; ++y, buffer += stride)
			for (int x = 0; x < width*4; x +=4) 
			{
				alpha = buffer [x + 3];  // Save off alpha
				temp = buffer [x];  // save off red

				if (alpha < 255) {
					buffer [x] = ConversionHelpers.UnpremultiplyValue (alpha, buffer [x + 2]);  // move blue to red
					buffer [x + 1] = ConversionHelpers.UnpremultiplyValue (alpha, buffer [x + 1]);
					buffer [x + 2] = ConversionHelpers.UnpremultiplyValue (alpha, temp);	// move the red to green
				} else {
					buffer [x] = buffer [x + 2];  // move blue to red
					buffer [x + 2] = temp;  // move the red to green
				}
			}
		}
			
		// Our internal format is pre-multiplied alpha
		static unsafe void Convert_BGRA_8888_To_P_RGBA_8888(IntPtr bitmapBlock, int width, int height, int stride)
		{
			byte temp = 0;
			byte alpha = 0;
			byte* buffer = (byte*)bitmapBlock;

			for (int y = 0; y < height; ++y, buffer += stride)
			for (int sd = 0; sd < width*4; sd+=4) 
			{
				alpha = buffer [sd + 3];
				temp = buffer [sd];  // save off blue
				if (alpha < 255) {					
					buffer [sd] = ConversionHelpers.PremultiplyValue (alpha, buffer [sd + 2]);  // move red back
					buffer [sd + 1] = ConversionHelpers.PremultiplyValue (alpha, buffer [sd + 1]);
					buffer [sd + 2] = ConversionHelpers.PremultiplyValue (alpha, temp);
				} else {
					buffer [sd] = buffer [sd + 2];  // move red back
					buffer [sd + 2] = temp;
				}
			}
		}
	}
}
