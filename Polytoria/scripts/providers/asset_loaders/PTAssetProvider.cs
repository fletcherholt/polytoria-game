// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
#if CREATOR
using Polytoria.Creator.Utils;
#endif
using Polytoria.Shared;
using Polytoria.Shared.AssetLoaders;
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Polytoria.Providers.AssetLoaders;

public class PTAssetProvider : IAssetProvider
{
	private const string RootUrl = Globals.ApiEndpoint + "v1/assets/";
	private const string ServeURL = RootUrl + "serve/";
	private const string ServeMeshURL = RootUrl + "serve-mesh/";
	private const string ServeAudioURL = RootUrl + "serve-audio/";
	private readonly PTHttpClient _client = new();

	private static readonly System.Collections.Generic.Dictionary<ResourceType, string> LocalFolders = new()
	{
		{ ResourceType.Mesh, "meshes" },
		{ ResourceType.Audio, "audio" },
		{ ResourceType.Decal, "decals" },
	};

	/// <summary>
	/// Satisfy an asset id from the project itself rather than the store.
	///
	/// Uploading a mesh needs a paid subscription, which makes it awkward to try
	/// anything out before committing money - the point of issue #25. A file
	/// carried in the project under that id is used instead when there is one.
	///
	/// Two places are checked. First the project's own file index, which is the
	/// one that matters: a file with a .meta giving it an id is packed into the
	/// .ptpacked and is present in every process that loads it, server and client
	/// alike, with nothing to configure. Second, a folder named by
	/// POLYTORIA_LOCAL_ASSETS, which is convenient while iterating.
	///
	/// Returns null when there is no local copy, and the normal store path runs.
	/// </summary>
	private static byte[]? ReadLocalAsset(CacheItem item)
	{
		if (!LocalFolders.TryGetValue(item.Type, out string? folder))
		{
			return null;
		}

		// In the project, by id. Works everywhere the project does.
		try
		{
			byte[]? packed = Polytoria.Datamodel.Services.IOService.Active?.ReadAssetBytesFromID(item.ID.ToString());
			if (packed != null && packed.Length > 0)
			{
				return packed;
			}
		}
		catch (System.Exception)
		{
			// No project loaded yet, or no such id: fall through.
		}

		string root = System.Environment.GetEnvironmentVariable("POLYTORIA_LOCAL_ASSETS") ?? "";
		if (string.IsNullOrWhiteSpace(root))
		{
			return null;
		}

		string[] extensions = item.Type switch
		{
			ResourceType.Mesh => [".glb", ".gltf"],
			ResourceType.Audio => [".mp3"],
			_ => [".png", ".jpg"],
		};

		foreach (string extension in extensions)
		{
			string path = System.IO.Path.Combine(root, folder, item.ID + extension);
			if (System.IO.File.Exists(path))
			{
				try
				{
					return System.IO.File.ReadAllBytes(path);
				}
				catch (System.Exception ex)
				{
					PT.PrintErr($"Could not read local asset {path}: {ex.Message}");
					return null;
				}
			}
		}
		return null;
	}

	public async Task<CacheItem> LoadResource(CacheItem item)
	{
#if CREATOR
		_client.DefaultRequestHeaders["Authorization"] = PolyCreatorAPI.Token;
#endif

		byte[]? local = ReadLocalAsset(item);
		byte[] buffer;
		if (local != null)
		{
			buffer = local;
			item.DirectURL = "";
		}
		else
		{
			string url = GetAssetServeURL(item.ID, item.Type);
			ServeResponse response = await _client.GetFromJsonAsync(url, ServeResponseGenerationContext.Default.ServeResponse);
			buffer = await _client.GetByteArrayAsync(response.Url);
			item.DirectURL = response.Url;
		}

		item.SizeBytes = buffer.LongLength;

		CacheItem result;
		switch (item.Type)
		{
			case ResourceType.Mesh:
				result = await LoadMesh(item, buffer);
				break;
			case ResourceType.Audio:
				{
					item.Resource = new AudioStreamMP3() { Data = buffer };
					result = item;
					break;
				}
			case ResourceType.Asset:
			case ResourceType.Decal:
			case ResourceType.AssetThumbnail:
			case ResourceType.PlaceThumbnail:
			case ResourceType.PlaceIcon:
			case ResourceType.UserThumbnail:
			case ResourceType.UserHeadshot:
			case ResourceType.GuildThumbnail:
			case ResourceType.GuildBanner:
				{
					Image image = await Task.Run(() => DecodeImage(buffer, item.Resize));
					item.Resource = ImageTexture.CreateFromImage(image);
					result = item;
					break;
				}
			default: throw new NotImplementedException();
		}

		return result;
	}

	public string GetAssetServeURL(uint id, ResourceType itemType)
	{
		string url = itemType switch
		{
			ResourceType.Mesh => ServeMeshURL + id,
			ResourceType.Asset => ServeURL + id + "/asset",
			ResourceType.Decal => ServeURL + id + "/decal",
			ResourceType.Audio => ServeAudioURL + id,
			ResourceType.AssetThumbnail => ServeURL + id + "/assetThumbnail",
			ResourceType.PlaceThumbnail => ServeURL + id + "/placeThumbnail",
			ResourceType.PlaceIcon => ServeURL + id + "/placeIcon",
			ResourceType.UserThumbnail => ServeURL + id + "/userAvatar",
			ResourceType.UserHeadshot => ServeURL + id + "/userAvatarHeadshot",
			ResourceType.GuildThumbnail => ServeURL + id + "/guildIcon",
			ResourceType.GuildBanner => ServeURL + id + "/guildBanner",
			_ => throw new NotImplementedException()
		};

		return url;
	}

	public void Dispose() { }

	private static async Task<CacheItem> LoadMesh(CacheItem item, byte[] buffer)
	{
		Node3D scene = await Task.Run(() =>
		{
			GltfDocument document = new();
			GltfState state = new() { CreateAnimations = true };

			document.AppendFromBuffer(buffer, null, state);

			return (Node3D)document.GenerateScene(state);
		});

		// Remove arbitrary nodes that may come with the GLTF (eg. Rigidbodies)
		RemoveNonMeshNodes(scene);

		// Set mipmap texture filter for meshes
		SetMipmapTextureFilter(scene);

		TaskCompletionSource<PackedScene> callback = new();

		Callable.From(() =>
		{
			try
			{
				PackedScene mesh = new();
				mesh.Pack(scene);
				callback.SetResult(mesh);
			}
			catch (Exception ex)
			{
				callback.SetException(ex);
			}
			finally
			{
				scene.Free();
			}
		}).CallDeferred();

		item.Resource = await callback.Task;

		return item;
	}

	private static Image DecodeImage(byte[] buffer, Vector2I? resize)
	{
		Image image = new();
		image.LoadPngFromBuffer(buffer);

		if (resize != null)
		{
			image.Resize(resize.Value.X, resize.Value.Y, Image.Interpolation.Lanczos);
		}

		FixAlphaEdgesIfNeeded(image);
		image.GenerateMipmaps();

		return image;
	}

	private static void FixAlphaEdgesIfNeeded(Image image)
	{
		if (image.DetectAlpha() == Image.AlphaMode.None) return;
		image.FixAlphaEdges();
	}

	private static void RemoveNonMeshNodes(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			RemoveNonMeshNodes(child); // recurse first

			if (child is not (MeshInstance3D or Skeleton3D or AnimationPlayer or AnimationTree or BoneAttachment3D) &&
				child.GetType() != typeof(Node3D))
			{
				child.Free();
			}
		}
	}

	private static void SetMipmapTextureFilter(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			SetMipmapTextureFilter(child);

			if (child is MeshInstance3D meshInstance)
			{
				for (int s = 0; s < meshInstance.Mesh.GetSurfaceCount(); s++)
				{
					if (meshInstance.GetActiveMaterial(s) is BaseMaterial3D material)
					{
						if (material.AlbedoTexture is ImageTexture albedoTex)
						{
							Image img = albedoTex.GetImage();
							FixAlphaEdgesIfNeeded(img);
							img.GenerateMipmaps();
							material.AlbedoTexture = ImageTexture.CreateFromImage(img);
						}

						material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;
					}
				}
			}
		}
	}
}

internal struct ServeResponse
{
	[JsonPropertyName("url")]
	public string Url { get; set; }
}

[JsonSerializable(typeof(ServeResponse))]
[JsonSerializable(typeof(string))]
internal partial class ServeResponseGenerationContext : JsonSerializerContext { }
