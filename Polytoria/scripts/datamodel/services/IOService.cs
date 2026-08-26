// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Attributes;
using System;
using System.IO;
using Godot;
#if CREATOR
using Polytoria.Shared;
using Polytoria.Utils;
#endif
using System.Collections.Generic;

namespace Polytoria.Datamodel.Services;

[Static("IO")]
[ExplorerExclude]
[SaveIgnore]
public sealed partial class IOService : Instance
{
	private const string PolyCreatorTempPath = "polyc_temp";
	private static readonly string[] AllowedExtensions = ["poly", "ptmd", "model", "lua", "luau", "json", "txt"];

	/// <summary>
	/// The IO service of the most recently loaded project.
	///
	/// The asset loader needs to reach a project's own files to satisfy an asset id
	/// from them, and it has no instance to work from - it is a plain provider on a
	/// background thread. World.Current is no use there either: it is a Creator
	/// concept and is null in a standalone client or server.
	/// </summary>
	public static IOService? Active { get; internal set; }

	internal Dictionary<string, byte[]> FileStructure = [];
	internal Dictionary<string, string> FileToIndex = [];
	internal Dictionary<string, string> IndexToFile = [];
	internal Dictionary<string, string> TempFileToIndex = [];
	internal Dictionary<string, string> TempIndexToFile = [];
	private static readonly string TempFilePath;

	static IOService()
	{
		TempFilePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), PolyCreatorTempPath));
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IORead)]
	public byte[]? ReadBytesFromPath(string path)
	{
		if (!AllowedExtensions.Contains(path.GetExtension())) throw new Exception("Reading this file extension is not allowed");
#if CREATOR
		if (Root.SessionType == World.SessionTypeEnum.Creator)
		{
			string baseFolder = Root.LinkedSession.ProjectFolderPath;
			if (path.StartsWith("@temp/"))
			{
				baseFolder = TempFilePath;
			}

			string rp = Path.GetFullPath(Path.Join(baseFolder, path)).SanitizePath();

			if (!PathUtils.IsPathInsideDirectory(rp, baseFolder))
			{
				PT.PrintErr("Tried to access file beyond the project folder. ", path);
				return null;
			}

			if (!File.Exists(rp))
			{
				return null;
			}

			try
			{
				byte[] content = File.ReadAllBytes(rp);
				return content;
			}
			catch (Exception ex)
			{
				PT.PrintErr(ex);
				return null;
			}
		}
#endif

		if (FileStructure.TryGetValue(path, out byte[]? val))
		{
			return val;
		}

		return null;
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IORead)]
	public string? ReadTextFromPath(string path)
	{
		return ReadBytesFromPath(path)?.GetStringFromUtf8();
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IOWrite)]
	public void WriteBytesToPath(string path, byte[] bytes)
	{
		if (!AllowedExtensions.Contains(path.GetExtension())) throw new Exception("Writing to this file extension is not allowed");
#if CREATOR
		if (Root.SessionType == World.SessionTypeEnum.Creator)
		{
			string baseFolder = Root.LinkedSession.ProjectFolderPath;
			if (path.StartsWith("@temp/"))
			{
				baseFolder = TempFilePath;
			}

			string rp = Path.GetFullPath(Path.Join(baseFolder, path)).SanitizePath();

			if (!PathUtils.IsPathInsideDirectory(rp, baseFolder))
			{
				PT.PrintErr("Tried to access file beyond the project folder. ", path);
				return;
			}

			string fileBaseFolder = rp.GetBaseDir();

			if (!Directory.Exists(fileBaseFolder))
			{
				Directory.CreateDirectory(fileBaseFolder);
			}

			File.WriteAllBytes(rp, bytes);
			Root.LinkedSession.QueueRescanFolder();
			return;
		}
#endif

		FileStructure[path] = bytes;
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IOWrite)]
	public void WriteTextToPath(string path, string txt)
	{
		WriteBytesToPath(path, txt.ToUtf8Buffer());
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IORead)]
	public string[] ListProjectFiles()
	{
#if CREATOR
		string[] files = Directory.GetFiles(Root.LinkedSession.ProjectFolderPath, "*", SearchOption.AllDirectories);
		List<string> finalFiles = [];
		foreach (string item in files)
		{
			finalFiles.Add(Path.GetRelativePath(Root.LinkedSession.ProjectFolderPath, item).SanitizePath());
		}
		return [.. finalFiles];
#else
		return [];
#endif
	}

	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IORead)]
	public byte[]? ReadBytesFromID(string id)
	{
		string? path = GetPathFromID(id);
		if (path == null) return null;
		return ReadBytesFromPath(path);
	}

	/// <summary>
	/// The bytes of a project file by id, for the engine's own use.
	///
	/// ReadBytesFromPath refuses any extension outside a small whitelist, which is
	/// there to stop a script reading whatever it likes out of a project. The asset
	/// loader is not a script, and the files it wants - .glb and the rest - are
	/// deliberately not on that list, so it comes through here instead. Nothing
	/// script-reachable calls this.
	/// </summary>
	internal byte[]? ReadAssetBytesFromID(string id)
	{
		string? path = GetPathFromID(id);
		if (path == null) return null;
		if (FileStructure.TryGetValue(path, out byte[]? packed)) return packed;
#if CREATOR
		try
		{
			if (Root?.LinkedSession?.ProjectFolderPath is string folder)
			{
				string full = Path.GetFullPath(Path.Join(folder, path)).SanitizePath();
				if (PathUtils.IsPathInsideDirectory(full, folder) && File.Exists(full))
				{
					return File.ReadAllBytes(full);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
#endif
		return null;
	}


	[ScriptMethod(Permissions = Scripting.ScriptPermissionFlags.IORead)]
	public string? GetPathFromID(string indexID)
	{
		if (indexID.StartsWith("temp:"))
			if (TempIndexToFile.TryGetValue(indexID, out string? spath)) return spath;
		if (IndexToFile.TryGetValue(indexID, out string? path)) return path;
		return null;
	}

	public string GetIDFromPath(string path)
	{
		// check temp first
		if (path.StartsWith("@temp"))
			if (TempFileToIndex.TryGetValue(path, out string? sid)) return sid;

		if (FileToIndex.TryGetValue(path, out string? id)) return id;
		string newId = Guid.NewGuid().ToString();

#if CREATOR
		// temporary
		if (path.StartsWith("@temp"))
		{
			newId = "temp:" + newId;
			TempFileToIndex[path] = newId;
			TempIndexToFile[newId] = path;
			return newId;
		}

		// New file index
		if (Root.LinkedSession != null)
		{
			Root.LinkedSession.IndexToFile[newId] = path;
			Root.LinkedSession.SaveFileIndex();
		}
#endif

		// Fallsafe for non opened games
		FileToIndex[path] = newId;
		IndexToFile[newId] = path;

		return newId;
	}
}
