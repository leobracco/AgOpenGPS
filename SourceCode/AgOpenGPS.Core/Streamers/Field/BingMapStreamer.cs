using AgLibrary.Logging;
using AgOpenGPS.Core.Interfaces;
using AgOpenGPS.Core.Models;
using System;
using System.IO;

namespace AgOpenGPS.Core.Streamers
{
    public class BingMapStreamer : FieldAspectStreamer
    {
        private readonly BingMapBitmapStreamer _bitmapStreamer;
        public BingMapStreamer() : base("BackPic.txt", null)
        {
            _bitmapStreamer = new BingMapBitmapStreamer();
        }

        public BingMap TryRead(DirectoryInfo fieldDirectory)
        {
            BingMap bingMap = null;
            FileInfo fileInfo = GetFileInfo(fieldDirectory);
            if (fileInfo.Exists)
            {
                try
                {
                    bingMap = Read(fieldDirectory);
                }
                catch (Exception)
                {
                }
            }
            return bingMap;
        }

        public void TryWrite(BingMap bingMap, DirectoryInfo fieldDirectory)
        {
            try
            {
                Write(bingMap, fieldDirectory);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message + "\n Cannot write to file.");
                Log.EventWriter("Saving BingMap" + e.ToString());
            }
        }

        private BingMap Read(DirectoryInfo fieldDirectory)
        {
            BingMap bingMap = null;
            FileInfo fileInfo = GetFileInfo(fieldDirectory);
            using (GeoStreamReader reader = new GeoStreamReader(fileInfo))
            {
                string line = reader.ReadLine(); // skip header
                bool hasBingMap = reader.ReadBool();
                if (hasBingMap)
                {
                    GeoBoundingBox geoBb = reader.ReadGeoBoundingBox();
                    byte[] pngBytes = _bitmapStreamer.Read(fieldDirectory);
                    if (pngBytes != null)
                    {
                        bingMap = new BingMap(geoBb, pngBytes);
                    }
                }
            }
            return bingMap;
        }

        private void Write(BingMap bingMap, DirectoryInfo fieldDirectory)
        {
            if (bingMap != null)
            {
                FileInfo boundingBoxFileInfo = GetFileInfo(fieldDirectory);
                using (GeoStreamWriter writer = new GeoStreamWriter(boundingBoxFileInfo))
                {
                    writer.WriteLine("$BackPic");
                    writer.WriteBool(true);
                    writer.WriteGeoBoundingBox(bingMap.GeoBoundingBox);
                }
                _bitmapStreamer.Write(bingMap.PngBytes, fieldDirectory);
            }
            else
            {
                DeleteFile(fieldDirectory);
                _bitmapStreamer.DeleteFile(fieldDirectory);
            }
        }

        public void CreateFile(DirectoryInfo fieldDirectory)
        {
            fieldDirectory.Create();
            using (StreamWriter writer = new StreamWriter(GetFileInfo(fieldDirectory).Name))
            {
            }
        }

        private class BingMapBitmapStreamer : FieldAspectStreamer
        {
            public BingMapBitmapStreamer() : base("BackPic.png", null)
            {
            }

            // IO plano de bytes PNG: sin System.Drawing (portable).
            public byte[] Read(DirectoryInfo fieldDirectory)
            {
                FileInfo fileInfo = GetFileInfo(fieldDirectory);
                return fileInfo.Exists ? File.ReadAllBytes(fileInfo.FullName) : null;
            }

            public void Write(byte[] pngBytes, DirectoryInfo fieldDirectory)
            {
                FileInfo fileInfo = GetFileInfo(fieldDirectory);
                if (fileInfo.Exists)
                {
                    fileInfo.Delete();
                }
                if (pngBytes != null) File.WriteAllBytes(fileInfo.FullName, pngBytes);
            }
        }
    }
}
