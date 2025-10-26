using System.IO;
using System.Net.Http.Headers;

namespace rofsc
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: rofsc (input_path) (output.rofs)");
                Exit();
            }

            string dirPath = args[0];
            if (!Directory.Exists(dirPath))
            {
                Console.WriteLine("Path not found: " + dirPath);
                Exit();
            }

            string outFile = args[1];
            if (File.Exists(outFile))
            {
                Console.WriteLine("File already exists: " + outFile);
                Exit();
            }

            EndianWriter writer = new EndianWriter(File.Open(outFile, FileMode.Create), Endianness.LittleEndian);

            // Header
            writer.WriteString("ROFS");
            writer.WriteInt32(-1);
            writer.WriteInt32(0x20);
            writer.Position += 0xC;
            writer.WriteInt32(-1);
            writer.WriteInt32(-1);

            // File system entries
            ushort i = 1;
            writer.WriteInt32(0x8000);
            ushort totalDirCount = (ushort)(GetRecursiveDirCount(dirPath) + 1);
            writer.WriteUInt16(0);
            writer.WriteUInt16(totalDirCount);
            writer.Position += 4;
            long lastNamePos = 0x8020;
            while (writer.Position < lastNamePos)
            {
                writer.WriteInt32(-1);
            }

            writer.Position = 0x28;
            ushort totalFileCount = 0;
            DirNode rootNode = new DirNode(0);
            List<string> filePaths = new List<string>();
            WriteDirectories(dirPath, filePaths, writer, ref lastNamePos, ref i, ref totalFileCount, ref rootNode, true, totalDirCount, 0);
            while (writer.Position % 2 != 0)
            {
                writer.WriteByte(0);
            }
            int fileInfoPos = (int)writer.Position;
            writer.Position = 0xC;
            writer.WriteInt32(fileInfoPos - 0x20);
            writer.WriteInt32(fileInfoPos);
            writer.WriteInt32(totalFileCount * 8);

            int totalFiles = filePaths.Count;
            writer.Position = fileInfoPos;
            long fileDataPos = fileInfoPos + totalFiles * 8;
            while (fileDataPos % 0x10 != 0)
            {
                fileDataPos++;
            }

            uint currentFileDataPos = (uint)fileDataPos;
            int filesWritten = 0;
            foreach (string file in filePaths)
            {
                FileInfo fileInfo = new FileInfo(file);
                if (fileInfo.Length >= uint.MaxValue)
                {
                    Console.WriteLine("File too big: " + file);
                    Exit();
                }

                writer.WriteUInt32(currentFileDataPos);
                long currentInfoPos = writer.Position;
                writer.Position = currentFileDataPos;
                writer.WriteStreamedBytesFromFile(file);
                uint currentFileEndPos = (uint)writer.Position;
                writer.WriteByte(0xFF);
                while (writer.Position % 4 != 0)
                {
                    writer.WriteByte(0xFF);
                }

                currentFileDataPos = (uint)writer.Position;
                writer.Position = currentInfoPos;
                writer.WriteUInt32(currentFileEndPos);

                filesWritten++;
                double progress = (filesWritten / (double)totalFiles) * 100;
                Console.Write($"\rCreating filesystem... {progress:F1}% ({filesWritten}/{totalFiles})");
            }

            while (writer.Position != fileDataPos)
            {
                writer.WriteByte(0xFF);
            }

            Console.WriteLine();
            writer.Close();
            Console.WriteLine("File written to: " + outFile);
            Exit();
        }

        static void Exit()
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(0);
        }

        static void WriteDirectories(string path, List<string> filePaths, EndianWriter writer, ref long lastNamePos, ref ushort i, ref ushort totalFileCount, ref DirNode parentNode, bool isRoot, ushort totalDirCount, ushort startIndex)
        {
            if (i >= 0xFFF)
            {
                Console.WriteLine("Directory limit exceeded");
                Exit();
            }

            ushort currentIndex = i;
            DirNode node = new DirNode(i);
            node.parent = parentNode;

            var directories = Directory.GetDirectories(path);
            if (directories.Count() > 0xFFF)
            {
                Console.WriteLine("Too many directories in: " + path);
                Exit();
            }

            ushort j = i;
            foreach (var dir in directories)
            {
                writer.Position = lastNamePos;
                string? dirName = Path.GetFileName(dir);
                if (dirName == null)
                {
                    Console.WriteLine("Invalid directory: " + dir);
                    Exit();
                }

                if (dirName.Length > 0x7F)
                {
                    Console.WriteLine("Directory name too long: " + dir);
                    Exit();
                }

                writer.WriteByte((byte)(dirName.Length | 0x80));
                writer.WriteString(dirName);
                writer.WriteUInt16((ushort)(j | 0xF000));
                lastNamePos = writer.Position;
                int nrSubdirs = GetRecursiveDirCount(dir);
                if (j + nrSubdirs + 1 > 0xFFF)
                {
                    Console.WriteLine("Too many directories in: " + dir);
                    Exit();
                }

                j += (ushort)(nrSubdirs + 1);
            }

            var files = Directory.GetFiles(path);
            foreach (var file in files)
            {
                string? fileName = Path.GetFileName(file);
                if (fileName == null)
                {
                    Console.WriteLine("Invalid file: " + file);
                    Exit();
                }

                if (fileName.Length > 0x7F)
                {
                    Console.WriteLine("File name too long: " + file);
                    Exit();
                }

                writer.WriteByte((byte)fileName.Length);
                writer.WriteString(fileName);
                filePaths.Add(file);
            }
            writer.WriteByte(0);
            lastNamePos = writer.Position;
            i++;

            if (totalFileCount + files.Length >= ushort.MaxValue)
            {
                Console.WriteLine("File limit exceeded");
                Exit();
            }

            totalFileCount += (ushort)files.Length;

            ushort globalIndex = (ushort)(i - startIndex - 1);
            bool isLast = globalIndex == totalDirCount;

            if (!isLast)
            {
                writer.Position = 0x28 + (i - 2) * 8;
                writer.WriteInt32((int)lastNamePos - 0x20);
                writer.WriteUInt16(totalFileCount);
            }

            if (!isRoot)
            {
                writer.Position = 0x2E + (i - 3) * 8;
                writer.WriteUInt16((ushort)(((node.parent?.id - 1) ?? 0) | 0xF000));
            }

            writer.Position = lastNamePos;

            foreach (var dir in directories)
            {
                WriteDirectories(dir, filePaths, writer, ref lastNamePos, ref i, ref totalFileCount, ref node, false, totalDirCount, startIndex);
            }

            if (node.parent != null)
                node = node.parent;
        }

        static int GetRecursiveDirCount(string path)
        {
            var dirs = Directory.GetDirectories(path);
            int nrDirs = dirs.Length;
            foreach (var dir in dirs)
            {
                nrDirs += GetRecursiveDirCount(dir);
            }

            return nrDirs;
        }
    }

    class DirNode
    {
        public ushort id;
        public DirNode? parent;

        public DirNode(ushort id)
        {
            this.id = id;
        }
    }
}