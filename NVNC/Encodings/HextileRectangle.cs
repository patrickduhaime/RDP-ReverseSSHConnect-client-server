#region

using System;
using System.Collections.Generic;
using System.IO;
using NVNC.Utils;

#endregion

namespace NVNC.Encodings
{
    /// <summary>
    ///     Implementation of Hextile encoding.
    /// </summary>
    public sealed class HextileRectangle : EncodedRectangle
    {
        private const int RAW = 0x01;
        private const int BACKGROUND_SPECIFIED = 0x02;
        private const int FOREGROUND_SPECIFIED = 0x04;
        private const int ANY_SUBRECTS = 0x08;
        private const int SUBRECTS_COLORED = 0x10;
        private readonly int[] pixels;

        private object[] tiles; // each element either Tile or byte[]

        public HextileRectangle(VncHost rfb, Framebuffer framebuffer, int[] pixels, Rectangle2 rectangle)
            : base(rfb, framebuffer, rectangle)
        {
            this.pixels = pixels;
        }

        private Tile ToTile(int[] pixeldata, int scanline, int x, int y, int w, int h)
        {
            var tile = new Tile();

            SubRect subrect;
            var vector = new List<SubRect>();

            int currentPixel;
            int currentX, currentY;
            int runningX, runningY;
            int firstX = 0, firstY, firstW, firstH;
            int secondX = 0, secondY, secondW, secondH;
            bool firstYflag;
            int segment;
            int line;
            tile.bgpixel = GetBackground(pixeldata, scanline, x, y, w, h);

            for (currentY = 0; currentY < h; currentY++)
            {
                line = (currentY + y)*scanline + x;
                for (currentX = 0; currentX < w; currentX++)
                {
                    if (pixeldata[line + currentX] != tile.bgpixel)
                    {
                        currentPixel = pixeldata[line + currentX];
                        firstY = currentY - 1;
                        firstYflag = true;
                        for (runningY = currentY; runningY < h; runningY++)
                        {
                            segment = (runningY + y)*scanline + x;
                            if (pixeldata[segment + currentX] != currentPixel)
                                break;
                            runningX = currentX;
                            while ((runningX < w) && (pixeldata[segment + runningX] == currentPixel))
                                runningX++;
                            runningX--;
                            if (runningY == currentY)
                                secondX = firstX = runningX;
                            if (runningX < secondX)
                                secondX = runningX;
                            if (firstYflag && (runningX >= firstX))
                                firstY++;
                            else
                                firstYflag = false;
                        }
                        secondY = runningY - 1;

                        firstW = firstX - currentX + 1;
                        firstH = firstY - currentY + 1;
                        secondW = secondX - currentX + 1;
                        secondH = secondY - currentY + 1;

                        subrect = new SubRect();
                        subrect.pixel = currentPixel;
                        subrect.x = currentX;
                        subrect.y = currentY;
                        vector.Add(subrect);

                        if (firstW*firstH > secondW*secondH)
                        {
                            subrect.w = firstW;
                            subrect.h = firstH;
                        }
                        else
                        {
                            subrect.w = secondW;
                            subrect.h = secondH;
                        }

                        for (runningY = subrect.y; runningY < subrect.y + subrect.h; runningY++)
                            for (runningX = subrect.x; runningX < subrect.x + subrect.w; runningX++)
                                pixeldata[(runningY + y)*scanline + x + runningX] = tile.bgpixel;
                    }
                }
            }

            tile.subrects = new SubRect[vector.Count];
            tile.subrects = vector.ToArray();
            return tile;
        }

        private byte[] Raw(int[] pixeldata, int scanline, int x, int y, int w, int h)
        {
            byte[] bytez = null;
            var b = 0;
            var i = 0;
            var s = 0;
            int pixel;
            var size = w*h;
            var jump = scanline - w;
            var p = y*scanline + x;
            switch (framebuffer.BitsPerPixel)
            {
                case 32:
                    bytez = new byte[size << 2];
                    for (; i < size; i++, s++, p++)
                    {
                        if (s == w)
                        {
                            s = 0;
                            p += jump;
                        }
                        pixel = framebuffer.TranslatePixel(pixeldata[p]);
                        bytez[b++] = (byte) (pixel & 0xFF);
                        bytez[b++] = (byte) ((pixel >> 8) & 0xFF);
                        bytez[b++] = (byte) ((pixel >> 16) & 0xFF);
                        bytez[b++] = (byte) ((pixel >> 24) & 0xFF);
                    }
                    break;
                case 16:
                    bytez = new byte[size << 1];
                    for (; i < size; i++, s++, p++)
                    {
                        if (s == w)
                        {
                            s = 0;
                            p += jump;
                        }
                        pixel = framebuffer.TranslatePixel(pixeldata[p]);
                        bytez[b++] = (byte) (pixel & 0xFF);
                        bytez[b++] = (byte) ((pixel >> 8) & 0xFF);
                    }
                    break;
                case 8:
                    bytez = new byte[size];
                    for (; i < size; i++, s++, p++)
                    {
                        if (s == w)
                        {
                            s = 0;
                            p += jump;
                        }
                        bytez[i] = (byte) framebuffer.TranslatePixel(pixeldata[p]);
                    }
                    break;
            }

            return bytez;
        }

        public override void Encode()
        {
            var x = 0; //rectangle.X;
            var y = 0; //rectangle.Y;
            var w = rectangle.Width;
            var h = rectangle.Height;

            //System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
            var vector = new List<object>();
            int currentX, currentY;
            int tileW, tileH;
            Tile tile1;
            //int tileMaxSize;

            //int pixelSize = framebuffer.BitsPerPixel >> 3; // div 8

            // Maximum size of raw tile
            //int rawMaxSize = pixelSize << 8; // * 16 * 16

            for (currentY = y; currentY < h; currentY += 16)
            {
                for (currentX = x; currentX < w; currentX += 16)
                {
                    // Tile size
                    tileW = w - currentX;
                    if (tileW > 16)
                        tileW = 16;
                    tileH = h - currentY;
                    if (tileH > 16)
                        tileH = 16;

                    tile1 = ToTile(pixels, w, currentX, currentY, tileW, tileH);

                    //tileMaxSize = tile1.subrects.Length * (2 + pixelSize) + (2 * pixelSize) + 1;
                    //if (tileMaxSize < rawMaxSize)
                    //{
                    vector.Add(tile1);
                    /*}
                    else
                    {
                        // Tile may be too large to be efficient, better use raw instead
                        vector.Add(Raw(pixels, w, currentX + x, currentY + y, tileW, tileH));
                        //System.err.print("!");
                    }*/
                }
            }

            tiles = new object[vector.Count];
            tiles = vector.ToArray();
        }

        public override void WriteData()
        {
            base.WriteData();
            rfb.WriteUInt32(Convert.ToUInt32(VncHost.Encoding.HextileEncoding));

            Tile tile;
            int mask;
            var oldBgpixel = 0x10000000;
            var fgpixel = 0x10000000;
            int j;

            //Console.WriteLine("Tiles: " + tiles.Length);

            //Writing to a MemoryStream is faster, than writing to a NetworkStream, while being read chunk by chunk
            //Data is sent fast, when it is sent as one ordered byte array
            using (var ms = new MemoryStream())
            {
                for (var i = 0; i < tiles.Length; i++)
                {
                    if (tiles[i] is Tile)
                    {
                        tile = (Tile) tiles[i];
                        mask = 0;

                        // Do we have subrects?				
                        if (tile.subrects.Length > 0)
                        {
                            // We have subrects
                            mask |= ANY_SUBRECTS;

                            // Do all subrects have the same pixel?
                            fgpixel = tile.subrects[0].pixel;
                            for (j = 1; j < tile.subrects.Length; j++)
                            {
                                if (tile.subrects[j].pixel != fgpixel)
                                {
                                    // Subrects are of varying colors
                                    mask |= SUBRECTS_COLORED;
                                    break;
                                }
                            }

                            if ((mask & SUBRECTS_COLORED) == 0)
                            {
                                // All subrects have the same pixel
                                mask |= FOREGROUND_SPECIFIED;
                            }
                        }

                        // Has the background changed?
                        if (tile.bgpixel != oldBgpixel)
                        {
                            oldBgpixel = tile.bgpixel;
                            mask |= BACKGROUND_SPECIFIED;
                        }

                        //pwriter.Write((byte)mask);
                        ms.WriteByte((byte) mask);

                        // Background pixel
                        if ((mask & BACKGROUND_SPECIFIED) != 0)
                        {
                            var pd = PixelGrabber.GrabBytes(tile.bgpixel, framebuffer);
                            ms.Write(pd, 0, pd.Length);

                            //pwriter.WritePixel(tile.bgpixel);
                        }

                        // Foreground pixel
                        if ((mask & FOREGROUND_SPECIFIED) != 0)
                        {
                            var pd = PixelGrabber.GrabBytes(fgpixel, framebuffer);
                            ms.Write(pd, 0, pd.Length);
                            //pwriter.WritePixel(fgpixel);
                        }

                        // Subrects
                        if ((mask & ANY_SUBRECTS) != 0)
                        {
                            ms.WriteByte((byte) tile.subrects.Length);
                            //pwriter.Write((byte)tile.subrects.Length);

                            //using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                            //{
                            for (j = 0; j < tile.subrects.Length; j++)
                            {
                                // Subrects colored
                                if ((mask & SUBRECTS_COLORED) != 0)
                                {
                                    var x = PixelGrabber.GrabBytes(tile.subrects[j].pixel, framebuffer);
                                    ms.Write(x, 0, x.Length);
                                }
                                ms.WriteByte((byte) ((tile.subrects[j].x << 4) | tile.subrects[j].y));
                                ms.WriteByte((byte) (((tile.subrects[j].w - 1) << 4) | (tile.subrects[j].h - 1)));
                            }
                            //pwriter.Write(ms.ToArray());
                            //}
                        }
                    }
                    else
                    {
                        ms.WriteByte(RAW);
                        //pwriter.Write((byte)RAW);

                        ms.Write((byte[]) tiles[i], 0, ((byte[]) tiles[i]).Length);
                        //pwriter.Write((byte[])tiles[i]);
                    }
                }
                rfb.Write(ms.ToArray());
            }
        }

        private class Tile
        {
            public int bgpixel;
            public SubRect[] subrects;
        }

        private class SubRect
        {
            public int h;
            public int pixel;
            public int w;
            public int x;
            public int y;
        }
    }
}