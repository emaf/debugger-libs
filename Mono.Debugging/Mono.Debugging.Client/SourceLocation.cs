using System;
using System.IO;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Mono.Debugging.Client
{
	[Serializable]
	public class SourceLink
	{
		readonly Dictionary<string, string> maps;

		/// <summary>
		/// Collection of KeyValues of original base path on disk and HTTP base path
		/// </summary>
		/// <param name="from">
		/// Original base path (with wildcard) used to build the .pdb file e.g.  f:/build/*
		/// </param>
		/// <param name="to">
		/// HTTP base path (with wildcard) where files may be downloaded from
		/// e.g. https://raw.githubusercontent.com/my-org/my-project/1111111111111111111111111111111111111111/*
		/// </param>
		public SourceLink (Dictionary<string, string> maps)
		{
			this.maps = maps;
		}

		public async Task<string> DownloadFile(string fileName, string cachePath)
		{
			foreach(var kv in maps) {
		      var 	pattern = kv.Key.Replace ("*", "").Replace ('\\', '/');

				if (fileName.StartsWith(pattern)) {
					var localPath = fileName.Replace (pattern.Replace (".*", ""), "");
					var httpBasePath = kv.Value.Replace ("*", "");
					// org/project-name/git-sha (usually)
					var pathAndQuery = new Uri (httpBasePath).PathAndQuery.Substring(1);
					var saveTo = Path.Combine (cachePath, pathAndQuery, localPath);
					// ~/Library/Caches/VisualStudio/8.0/Symbols/org/projectname/git-sha/path/to/file.cs
					if (!File.Exists (saveTo)) {
						Directory.GetParent (saveTo).Create ();
						// Replace something like "f:/build/*" with "https://raw.githubusercontent.com/org/projectname/git-sha/*"
						var httpPath = Regex.Replace (fileName, pattern, httpBasePath);
						var client = new WebClient ();
						await client.DownloadFileTaskAsync (httpPath, saveTo);
					}
					return saveTo;
				}

			}
			return null;
		}
	}

	[Serializable]
	public class SourceLocation
	{
		public string MethodName { get; private set; }
		public string FileName { get; private set; }
		public int Line { get; private set; }
		public int Column { get; private set; }
		public int EndLine { get; private set; }
		public int EndColumn { get; private set; }
		public byte[] FileHash { get; private set; }
		public SourceLink SourceLink { get; private set; }

		[Obsolete]
		public SourceLocation (string methodName, string fileName, int line)
			: this (methodName, fileName, line, -1, -1, -1, null)
		{
		}

		public SourceLocation (string methodName, string fileName, int line, int column, int endLine, int endColumn, byte[] hash = null, SourceLink sourceLink = null)
		{
			this.MethodName = methodName;
			this.FileName = fileName;
			this.Line = line;
			this.Column = column;
			this.EndLine = endLine;
			this.EndColumn = endColumn;
			this.FileHash = hash;
			this.SourceLink = sourceLink;
		}
		
		public override string ToString ()
		{
			return string.Format("[SourceLocation Method={0}, Filename={1}, Line={2}, Column={3}]", MethodName, FileName, Line, Column);
		}

		static void ComputeHashes (Stream stream, HashAlgorithm hash, HashAlgorithm dos, HashAlgorithm unix)
		{
			var unixBuffer = ArrayPool<byte>.Shared.Rent (4096 + 1);
			var dosBuffer = ArrayPool<byte>.Shared.Rent (8192 + 1);
			var buffer = ArrayPool<byte>.Shared.Rent (4096);
			byte pc = 0;
			int count;

			try {
				while ((count = stream.Read (buffer, 0, buffer.Length)) > 0) {
					int unixIndex = 0, dosIndex = 0;

					for (int i = 0; i < count; i++) {
						var c = buffer[i];

						if (c == (byte) '\r') {
							if (pc == (byte) '\r')
								unixBuffer[unixIndex++] = pc;
							dosBuffer[dosIndex++] = c;
						} else if (c == (byte) '\n') {
							if (pc != (byte) '\r')
								dosBuffer[dosIndex++] = (byte) '\r';
							unixBuffer[unixIndex++] = c;
							dosBuffer[dosIndex++] = c;
						} else {
							if (pc == (byte) '\r')
								unixBuffer[unixIndex++] = pc;
							unixBuffer[unixIndex++] = c;
							dosBuffer[dosIndex++] = c;
						}

						pc = c;
					}

					hash.TransformBlock (buffer, 0, count, outputBuffer: null, outputOffset: 0);
					dos.TransformBlock (dosBuffer, 0, dosIndex, outputBuffer: null, outputOffset: 0);
					unix.TransformBlock (unixBuffer, 0, unixIndex, outputBuffer: null, outputOffset: 0);
				}

				hash.TransformFinalBlock (buffer, 0, 0);
				dos.TransformFinalBlock (buffer, 0, 0);
				unix.TransformFinalBlock (buffer, 0, 0);
			} finally {
				ArrayPool<byte>.Shared.Return (unixBuffer);
				ArrayPool<byte>.Shared.Return (dosBuffer);
				ArrayPool<byte>.Shared.Return (buffer);
			}
		}

		static bool ChecksumsEqual (byte[] calculated, byte[] checksum, int skip = 0)
		{
			if (skip > 0) {
				if (calculated.Length < checksum.Length - skip)
					return false;
			} else {
				if (calculated.Length != checksum.Length)
					return false;
			}

			for (int i = 0, csi = skip; csi < checksum.Length; i++, csi++) {
				if (calculated[i] != checksum[csi])
					return false;
			}

			return true;
		}

		static bool CheckHash (Stream stream, string algorithm, byte[] checksum)
		{
			using (var hash = HashAlgorithm.Create (algorithm)) {
				int size = hash.HashSize / 8;

				using (var dos = HashAlgorithm.Create (algorithm)) {
					using (var unix = HashAlgorithm.Create (algorithm)) {
						stream.Position = 0;

						ComputeHashes (stream, hash, dos, unix);

						if (checksum [0] == size && checksum.Length < size) {
							return ChecksumsEqual (hash.Hash, checksum, 1) ||
								ChecksumsEqual (unix.Hash, checksum, 1) ||
								ChecksumsEqual (dos.Hash, checksum, 1);
						}

						return ChecksumsEqual (hash.Hash, checksum) ||
							ChecksumsEqual (unix.Hash, checksum) ||
							ChecksumsEqual (dos.Hash, checksum);
					}
				}
			}
		}

		public static bool CheckFileHash (string path, byte[] checksum)
		{
			if (checksum == null || checksum.Length == 0 || !File.Exists (path))
				return false;

			using (var stream = File.OpenRead (path)) {
				if (checksum.Length == 16) {
					// Note: Roslyn SHA1 hashes are 16 bytes and start w/ 20
					if (checksum[0] == 20 && CheckHash (stream, "SHA1", checksum))
						return true;

					// Note: Roslyn SHA256 hashes are 16 bytes and start w/ 32
					if (checksum[0] == 32 && CheckHash (stream, "SHA256", checksum))
						return true;

					return CheckHash (stream, "MD5", checksum);
				}

				if (checksum.Length == 20)
					return CheckHash (stream, "SHA1", checksum);

				if (checksum.Length == 32)
					return CheckHash (stream, "SHA256", checksum);
			}

			return false;
		}
	}
}
