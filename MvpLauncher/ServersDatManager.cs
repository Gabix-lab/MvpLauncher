using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MvpLauncher
{
    public static class ServersDatManager
    {
        public static void EnsureServerExists(string filePath, string serverName, string serverIp)
        {
            var servers = new List<ServerEntry>();

            if (File.Exists(filePath))
            {
                try
                {
                    servers = ReadFile(filePath);
                }
                catch
                {
                    servers = new List<ServerEntry>();
                }
            }

            bool exists = false;
            foreach (var s in servers)
            {
                if (string.Equals(s.Ip, serverIp, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                servers.Add(new ServerEntry { Name = serverName, Ip = serverIp });
                WriteFile(filePath, servers);
            }
        }

        private static List<ServerEntry> ReadFile(string path)
        {
            var list = new List<ServerEntry>();

            using var fileStream = File.OpenRead(path);
            using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);

            byte rootType = reader.ReadByte();
            if (rootType != 10) return list;

            ReadString(reader);

            while (true)
            {
                byte tagType = reader.ReadByte();
                if (tagType == 0) break;

                string tagName = ReadString(reader);

                if (tagType == 9 && tagName == "servers")
                {
                    byte listElementType = reader.ReadByte();
                    int count = ReadInt32BigEndian(reader);

                    for (int i = 0; i < count; i++)
                    {
                        if (listElementType == 10)
                        {
                            var entry = ReadServerCompound(reader);
                            if (entry != null) list.Add(entry);
                        }
                        else
                        {
                            SkipTag(reader, listElementType);
                        }
                    }
                }
                else
                {
                    SkipTag(reader, tagType);
                }
            }

            return list;
        }

        private static ServerEntry? ReadServerCompound(BinaryReader reader)
        {
            var entry = new ServerEntry();

            while (true)
            {
                byte tagType = reader.ReadByte();
                if (tagType == 0) break;

                string tagName = ReadString(reader);

                if (tagType == 8)
                {
                    string value = ReadString(reader);
                    if (tagName == "name") entry.Name = value;
                    else if (tagName == "ip") entry.Ip = value;
                }
                else
                {
                    SkipTag(reader, tagType);
                }
            }

            return string.IsNullOrEmpty(entry.Ip) ? null : entry;
        }

        private static void WriteFile(string path, List<ServerEntry> servers)
        {
            using var fileStream = File.Create(path);
            using var gzip = new GZipStream(fileStream, CompressionMode.Compress);
            using var writer = new BinaryWriter(gzip, Encoding.UTF8);

            writer.Write((byte)10);
            WriteString(writer, "");

            writer.Write((byte)9);
            WriteString(writer, "servers");
            writer.Write((byte)10);
            WriteInt32BigEndian(writer, servers.Count);

            foreach (var s in servers)
            {
                writer.Write((byte)8);
                WriteString(writer, "name");
                WriteString(writer, s.Name);

                writer.Write((byte)8);
                WriteString(writer, "ip");
                WriteString(writer, s.Ip);

                writer.Write((byte)1);
                WriteString(writer, "acceptTextures");
                writer.Write((byte)1);

                writer.Write((byte)1);
                WriteString(writer, "hideAddress");
                writer.Write((byte)0);

                writer.Write((byte)0);
            }

            writer.Write((byte)0);
        }

        private static string ReadString(BinaryReader reader)
        {
            ushort len = ReadUInt16BigEndian(reader);
            if (len == 0) return "";
            var bytes = reader.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteString(BinaryWriter writer, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteUInt16BigEndian(writer, (ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            return (ushort)((bytes[0] << 8) | bytes[1]);
        }

        private static void WriteUInt16BigEndian(BinaryWriter writer, ushort value)
        {
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        private static void WriteInt32BigEndian(BinaryWriter writer, int value)
        {
            writer.Write((byte)((value >> 24) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }

        private static void SkipTag(BinaryReader reader, byte tagType)
        {
            switch (tagType)
            {
                case 1: reader.ReadByte(); break;
                case 2: reader.ReadBytes(2); break;
                case 3: reader.ReadBytes(4); break;
                case 4: reader.ReadBytes(8); break;
                case 5: reader.ReadBytes(4); break;
                case 6: reader.ReadBytes(8); break;
                case 7:
                    int blen = ReadInt32BigEndian(reader);
                    reader.ReadBytes(blen);
                    break;
                case 8:
                    ReadString(reader);
                    break;
                case 9:
                    byte elemType = reader.ReadByte();
                    int count = ReadInt32BigEndian(reader);
                    for (int i = 0; i < count; i++)
                        SkipTag(reader, elemType);
                    break;
                case 10:
                    while (true)
                    {
                        byte childType = reader.ReadByte();
                        if (childType == 0) break;
                        ReadString(reader);
                        SkipTag(reader, childType);
                    }
                    break;
                case 11:
                    int ilen = ReadInt32BigEndian(reader);
                    reader.ReadBytes(ilen * 4);
                    break;
                case 12:
                    int llen = ReadInt32BigEndian(reader);
                    reader.ReadBytes(llen * 8);
                    break;
            }
        }

        private class ServerEntry
        {
            public string Name { get; set; } = "";
            public string Ip { get; set; } = "";
        }
    }
}