﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GLTF.Schema;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF.Extensions;
using CameraType = GLTF.Schema.CameraType;
using WrapMode = GLTF.Schema.WrapMode;

namespace UnityGLTF
{
	public class ExportOptions
	{
		public GLTFSceneExporter.RetrieveTexturePathDelegate TexturePathRetriever = (texture) => texture.name;
		public bool ExportInactivePrimitives = true;
	}

	public class GLTFSceneExporter
	{
		public delegate string RetrieveTexturePathDelegate(Texture texture);

		private enum IMAGETYPE
		{
			RGB,
			RGBA,
			R,
			G,
			B,
			A,
			G_INVERT
		}

		private enum TextureMapType
		{
			Main,
			Bump,
			SpecGloss,
			Emission,
			MetallicGloss,
			Light,
			Occlusion
		}

		private struct ImageInfo
		{
			public Texture2D texture;
			public TextureMapType textureMapType;
		}
			
		public class PropertyCurveBindings
		{
			public string name;
			public List<EditorCurveBinding> curveBindings;

			public PropertyCurveBindings(string name, EditorCurveBinding curveBinding)
			{
				this.name = name;
				this.curveBindings = new List<EditorCurveBinding>
				{
					curveBinding
				};
			}
		}
		
		public class CurveBindingGroup
		{
			public string path;
			public Type type;
			public List<PropertyCurveBindings> properties;

			public CurveBindingGroup(string path, Type type, PropertyCurveBindings property)
			{
				this.path = path;
				this.type = type;
				this.properties = new List<PropertyCurveBindings>
				{
					property
				};
			}
		}

		private Transform[] _rootTransforms;
		private GLTFRoot _root;
		private BufferId _bufferId;
		private GLTFBuffer _buffer;
		private BinaryWriter _bufferWriter;
		private List<ImageInfo> _imageInfos;
		private List<Texture> _textures;
		private List<Material> _materials;
		private bool _shouldUseInternalBufferForImages;

		private ExportOptions _exportOptions;

		private Material _metalGlossChannelSwapMaterial;
		private Material _normalChannelMaterial;

		private const uint MagicGLTF = 0x46546C67;
		private const uint Version = 2;
		private const uint MagicJson = 0x4E4F534A;
		private const uint MagicBin = 0x004E4942;
		private const int GLTFHeaderSize = 12;
		private const int SectionHeaderSize = 8;

		protected struct PrimKey
		{
			public Mesh Mesh;
			public Material Material;
		}
		private readonly Dictionary<PrimKey, MeshId> _primOwner = new Dictionary<PrimKey, MeshId>();
		private readonly Dictionary<Mesh, MeshPrimitive[]> _meshToPrims = new Dictionary<Mesh, MeshPrimitive[]>();

		private readonly Dictionary<int, NodeId> _existedNodes = new Dictionary<int, NodeId>();
		private readonly Dictionary<int, AnimationId> _existedAnimations = new Dictionary<int, AnimationId>();
		private readonly Dictionary<int, SkinId> _existedSkins = new Dictionary<int, SkinId>();

		// Settings
		public static bool ExportNames = true;
		public static bool ExportFullPath = true;
		public static bool RequireExtensions = false;
		public static bool ExportPhysicsColliders = true;

		/// <summary>
		/// Create a GLTFExporter that exports out a transform
		/// </summary>
		/// <param name="rootTransforms">Root transform of object to export</param>
		[Obsolete("Please switch to GLTFSceneExporter(Transform[] rootTransforms, ExportOptions options).  This constructor is deprecated and will be removed in a future release.")]
		public GLTFSceneExporter(Transform[] rootTransforms, RetrieveTexturePathDelegate texturePathRetriever)
			: this(rootTransforms, new ExportOptions { TexturePathRetriever = texturePathRetriever })
		{
		}

		/// <summary>
		/// Create a GLTFExporter that exports out a transform
		/// </summary>
		/// <param name="rootTransforms">Root transform of object to export</param>
		public GLTFSceneExporter(Transform[] rootTransforms, ExportOptions options)
		{
			_exportOptions = options;

			var metalGlossChannelSwapShader = Resources.Load("MetalGlossChannelSwap", typeof(Shader)) as Shader;
			_metalGlossChannelSwapMaterial = new Material(metalGlossChannelSwapShader);

			var normalChannelShader = Resources.Load("NormalChannel", typeof(Shader)) as Shader;
			_normalChannelMaterial = new Material(normalChannelShader);

			_rootTransforms = rootTransforms;
			_root = new GLTFRoot
			{
				Accessors = new List<Accessor>(),
				Asset = new Asset
				{
					Version = "2.0"
				},
				Buffers = new List<GLTFBuffer>(),
				BufferViews = new List<BufferView>(),
				Cameras = new List<GLTFCamera>(),
				Images = new List<GLTFImage>(),
				Materials = new List<GLTFMaterial>(),
				Meshes = new List<GLTFMesh>(),
				Nodes = new List<Node>(),
				Samplers = new List<Sampler>(),
				Scenes = new List<GLTFScene>(),
				Textures = new List<GLTFTexture>(),
				Skins = new List<Skin>(),
				Animations = new List<GLTFAnimation>()
			};

			_imageInfos = new List<ImageInfo>();
			_materials = new List<Material>();
			_textures = new List<Texture>();

			_buffer = new GLTFBuffer();
			_bufferId = new BufferId
			{
				Id = _root.Buffers.Count,
				Root = _root
			};
			_root.Buffers.Add(_buffer);
		}

		/// <summary>
		/// Gets the root object of the exported GLTF
		/// </summary>
		/// <returns>Root parsed GLTF Json</returns>
		public GLTFRoot GetRoot()
		{
			return _root;
		}

		/// <summary>
		/// Writes a binary GLB file with filename at path.
		/// </summary>
		/// <param name="path">File path for saving the binary file</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLB(string path, string fileName, bool exportTransform = true)
		{
			_shouldUseInternalBufferForImages = true;
			string fullPath = Path.Combine(path, Path.ChangeExtension(fileName, "glb"));
			
			using (FileStream glbFile = new FileStream(fullPath, FileMode.Create))
			{
				SaveGLBToStream(glbFile, fileName, exportTransform);
			}

			if (!_shouldUseInternalBufferForImages)
			{
				ExportImages(path);
			}
		}

		/// <summary>
		/// In-memory GLB creation helper. Useful for platforms where no filesystem is available (e.g. WebGL).
		/// </summary>
		/// <param name="sceneName"></param>
		/// <returns></returns>
		public byte[] SaveGLBToByteArray(string sceneName)
		{
			using (var stream = new MemoryStream())
			{
				SaveGLBToStream(stream, sceneName, true);
				return stream.ToArray();
			}
		}

		/// <summary>
		/// Writes a binary GLB file into a stream (memory stream, filestream, ...)
		/// </summary>
		/// <param name="path">File path for saving the binary file</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLBToStream(Stream stream, string sceneName, bool exportTransform)
		{
			Stream binStream = new MemoryStream();
			Stream jsonStream = new MemoryStream();

			_bufferWriter = new BinaryWriter(binStream);

			TextWriter jsonWriter = new StreamWriter(jsonStream, Encoding.ASCII);

			_root.Scene = ExportScene(sceneName, _rootTransforms, exportTransform);

			_buffer.ByteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Length, 4);

			_root.Serialize(jsonWriter, true);

			_bufferWriter.Flush();
			jsonWriter.Flush();

			// align to 4-byte boundary to comply with spec.
			AlignToBoundary(jsonStream);
			AlignToBoundary(binStream, 0x00);

			int glbLength = (int)(GLTFHeaderSize + SectionHeaderSize +
				jsonStream.Length + SectionHeaderSize + binStream.Length);

			BinaryWriter writer = new BinaryWriter(stream);

			// write header
			writer.Write(MagicGLTF);
			writer.Write(Version);
			writer.Write(glbLength);

			// write JSON chunk header.
			writer.Write((int)jsonStream.Length);
			writer.Write(MagicJson);

			jsonStream.Position = 0;
			CopyStream(jsonStream, writer);

			writer.Write((int)binStream.Length);
			writer.Write(MagicBin);

			binStream.Position = 0;
			CopyStream(binStream, writer);

			writer.Flush();
		}

		/// <summary>
		/// Convenience function to copy from a stream to a binary writer, for
		/// compatibility with pre-.NET 4.0.
		/// Note: Does not set position/seek in either stream. After executing,
		/// the input buffer's position should be the end of the stream.
		/// </summary>
		/// <param name="input">Stream to copy from</param>
		/// <param name="output">Stream to copy to.</param>
		private static void CopyStream(Stream input, BinaryWriter output)
		{
			byte[] buffer = new byte[8 * 1024];
			int length;
			while ((length = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				output.Write(buffer, 0, length);
			}
		}

		/// <summary>
		/// Pads a stream with additional bytes.
		/// </summary>
		/// <param name="stream">The stream to be modified.</param>
		/// <param name="pad">The padding byte to append. Defaults to ASCII
		/// space (' ').</param>
		/// <param name="boundary">The boundary to align with, in bytes.
		/// </param>
		private static void AlignToBoundary(Stream stream, byte pad = (byte)' ', uint boundary = 4)
		{
			uint currentLength = (uint)stream.Length;
			uint newLength = CalculateAlignment(currentLength, boundary);
			for (int i = 0; i < newLength - currentLength; i++)
			{
				stream.WriteByte(pad);
			}
		}

		/// <summary>
		/// Calculates the number of bytes of padding required to align the
		/// size of a buffer with some multiple of byteAllignment.
		/// </summary>
		/// <param name="currentSize">The current size of the buffer.</param>
		/// <param name="byteAlignment">The number of bytes to align with.</param>
		/// <returns></returns>
		public static uint CalculateAlignment(uint currentSize, uint byteAlignment)
		{
			return (currentSize + byteAlignment - 1) / byteAlignment * byteAlignment;
		}


		/// <summary>
		/// Specifies the path and filename for the GLTF Json and binary
		/// </summary>
		/// <param name="path">File path for saving the GLTF and binary files</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLTFandBin(string path, string fileName, bool exportTransform = true)
		{
			_shouldUseInternalBufferForImages = false;
			var binFile = File.Create(Path.Combine(path, fileName + ".bin"));
			_bufferWriter = new BinaryWriter(binFile);

			_root.Scene = ExportScene(fileName, _rootTransforms, exportTransform);
			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			_buffer.Uri = fileName + ".bin";
			_buffer.ByteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Length, 4);

			var gltfFile = File.CreateText(Path.Combine(path, fileName + ".gltf"));
			_root.Serialize(gltfFile);

#if WINDOWS_UWP
			gltfFile.Dispose();
			binFile.Dispose();
#else
			gltfFile.Close();
			binFile.Close();
#endif
			ExportImages(path);
		}

		private Texture2D FlipTexture(Texture source, Texture2D flipped, RenderTextureFormat format, RenderTextureReadWrite readWrite)
		{
			var flippedRenderTexture = RenderTexture.GetTemporary(source.width, source.height, 0, format, readWrite);
			Graphics.Blit(source, flippedRenderTexture, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
			flipped.name = source.name;
			flipped.ReadPixels(new Rect(0, 0, flippedRenderTexture.width, flippedRenderTexture.height), 0, 0);
			flipped.Apply();
			RenderTexture.ReleaseTemporary(flippedRenderTexture);
			return flipped;
		}

		private void ExportImages(string outputPath)
		{
			for (int t = 0; t < _imageInfos.Count; ++t)
			{
				var image = _imageInfos[t].texture;
				int height = image.height;
				int width = image.width;

				switch (_imageInfos[t].textureMapType)
				{
					case TextureMapType.MetallicGloss:
						ExportMetallicGlossTexture(image, outputPath);
						break;
					case TextureMapType.Bump:
						ExportNormalTexture(image, outputPath);
						break;
					default:
						ExportTexture(image, outputPath);
						break;
				}
			}
		}

		/// <summary>
		/// This converts Unity's metallic-gloss texture representation into GLTF's metallic-roughness specifications.
		/// Unity's metallic-gloss A channel (glossiness) is inverted and goes into GLTF's metallic-roughness G channel (roughness).
		/// Unity's metallic-gloss R channel (metallic) goes into GLTF's metallic-roughess B channel.
		/// </summary>
		/// <param name="texture">Unity's metallic-gloss texture to be exported</param>
		/// <param name="outputPath">The location to export the texture</param>
		private void ExportMetallicGlossTexture(Texture2D texture, string outputPath)
		{
			var flipped = new Texture2D(texture.width, texture.height);
			texture = FlipTexture(texture, flipped, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

			Graphics.Blit(texture, destRenderTexture, _metalGlossChannelSwapMaterial);

			var exportTexture = new Texture2D(texture.width, texture.height);
			exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
			exportTexture.Apply();

			var finalFilenamePath = ConstructImageFilenamePath(texture, outputPath);
			File.WriteAllBytes(finalFilenamePath, exportTexture.EncodeToPNG());

			RenderTexture.ReleaseTemporary(destRenderTexture);
			if (Application.isEditor)
			{
				GameObject.DestroyImmediate(exportTexture);
				GameObject.DestroyImmediate(flipped);
			}
			else
			{
				GameObject.Destroy(exportTexture);
				GameObject.Destroy(flipped);
			}
		}

		/// <summary>
		/// This export's the normal texture. If a texture is marked as a normal map, the values are stored in the A and G channel.
		/// To output the correct normal texture, the A channel is put into the R channel.
		/// </summary>
		/// <param name="texture">Unity's normal texture to be exported</param>
		/// <param name="outputPath">The location to export the texture</param>
		private void ExportNormalTexture(Texture2D texture, string outputPath)
		{
			var flipped = new Texture2D(texture.width, texture.height);
			texture = FlipTexture(texture, flipped, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

			Graphics.Blit(texture, destRenderTexture, _normalChannelMaterial);

			var exportTexture = new Texture2D(texture.width, texture.height);
			exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
			exportTexture.Apply();

			var finalFilenamePath = ConstructImageFilenamePath(texture, outputPath);
			File.WriteAllBytes(finalFilenamePath, exportTexture.EncodeToPNG());

			RenderTexture.ReleaseTemporary(destRenderTexture);
			if (Application.isEditor)
			{
				GameObject.DestroyImmediate(exportTexture);
				GameObject.DestroyImmediate(flipped);
			}
			else
			{
				GameObject.Destroy(exportTexture);
				GameObject.Destroy(flipped);
			}
		}

		private void ExportTexture(Texture2D texture, string outputPath)
		{
			var flipped = new Texture2D(texture.width, texture.height);
			texture = FlipTexture(texture, flipped, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

			Graphics.Blit(texture, destRenderTexture);

			var exportTexture = new Texture2D(texture.width, texture.height);
			exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
			exportTexture.Apply();

			var finalFilenamePath = ConstructImageFilenamePath(texture, outputPath);
			File.WriteAllBytes(finalFilenamePath, exportTexture.EncodeToPNG());

			RenderTexture.ReleaseTemporary(destRenderTexture);
			if (Application.isEditor)
			{
				GameObject.DestroyImmediate(exportTexture);
				GameObject.DestroyImmediate(flipped);
			}
			else
			{
				GameObject.Destroy(exportTexture);
				GameObject.Destroy(flipped);
			}
		}

		private string ConstructImageFilenamePath(Texture2D texture, string outputPath)
		{
			var imagePath = _exportOptions.TexturePathRetriever(texture);
			if (string.IsNullOrEmpty(imagePath))
			{
				imagePath = Path.Combine(outputPath, texture.name);
			}

			var filenamePath = Path.Combine(outputPath, imagePath);
			if (!ExportFullPath)
			{
				filenamePath = outputPath + "/" + texture.name;
			}
			var file = new FileInfo(filenamePath);
			file.Directory.Create();
			return Path.ChangeExtension(filenamePath, ".png");
		}

		private SceneId ExportScene(string name, Transform[] rootObjTransforms, bool exportTransform)
		{
			var scene = new GLTFScene();

			if (ExportNames)
			{
				scene.Name = name;
			}

			scene.Nodes = new List<NodeId>(rootObjTransforms.Length);
			foreach (var transform in rootObjTransforms)
			{
				scene.Nodes.Add(ExportNode(transform, exportTransform));
			}

			_root.Scenes.Add(scene);

			return new SceneId
			{
				Id = _root.Scenes.Count - 1,
				Root = _root
			};
		}

		private NodeId ExportNode(Transform nodeTransform, bool exportTransform = true)
		{
			var node = new Node();

			if (ExportNames)
			{
				node.Name = nodeTransform.name;
			}

			//export camera attached to node
			Camera unityCamera = nodeTransform.GetComponent<Camera>();
			if (unityCamera != null)
			{
				node.Camera = ExportCamera(unityCamera);
			}

			if (exportTransform)
			{
				node.SetUnityTransform(nodeTransform);
			}
			else
			{
				var lastPos = nodeTransform.position;
				var lastQuat = nodeTransform.rotation;
				nodeTransform.position = Vector3.zero;
				nodeTransform.rotation = Quaternion.identity;
				node.SetUnityTransform(nodeTransform);
				nodeTransform.position = lastPos;
				nodeTransform.rotation = lastQuat;
			}

			var id = new NodeId
			{
				Id = _root.Nodes.Count,
				Root = _root
			};
			_root.Nodes.Add(node);

			_existedNodes.Add(nodeTransform.GetInstanceID(), id);

			// children that are primitives get put in a mesh
			GameObject[] meshPrimitives, skinnedMeshPrimitives, nonPrimitives;
			FilterPrimitives(nodeTransform, out meshPrimitives, out skinnedMeshPrimitives, out nonPrimitives);

			// children that are not primitives get added as child nodes
			if (nonPrimitives.Length > 0)
			{
				node.Children = new List<NodeId>(nonPrimitives.Length);
				foreach (var child in nonPrimitives)
				{
					node.Children.Add(ExportNode(child.transform));
				}
			}

			if (meshPrimitives.Length + skinnedMeshPrimitives.Length > 0)
			{
				if (skinnedMeshPrimitives.Length > 0)
				{
					ExportSkin(nodeTransform.name, skinnedMeshPrimitives, node);
				}

				if (meshPrimitives.Length > 0)
				{
					node.Mesh = ExportMesh(nodeTransform.name, meshPrimitives);
				}

				var primitives = new List<GameObject>();
				primitives.AddRange(meshPrimitives);
				primitives.AddRange(skinnedMeshPrimitives);
				// associate unity meshes with gltf mesh id
				foreach (var prim in primitives)
				{
					var smr = prim.GetComponent<SkinnedMeshRenderer>();
					if (smr != null)
					{
						_primOwner[new PrimKey { Mesh = smr.sharedMesh, Material = smr.sharedMaterial }] = node.Mesh;
					}
					else
					{
						var filter = prim.GetComponent<MeshFilter>();
						var renderer = prim.GetComponent<MeshRenderer>();
						_primOwner[new PrimKey { Mesh = filter.sharedMesh, Material = renderer.sharedMaterial }] = node.Mesh;
					}
				}
			}

			var animator = nodeTransform.gameObject.GetComponent<Animator>();
			if (animator != null)
			{
				if (animator.runtimeAnimatorController != null)
				{	
					var animationClips = animator.runtimeAnimatorController.animationClips;
					if (animationClips != null && animationClips.Length != 0)
					{
						ExportAnimations(animationClips, nodeTransform);
					}
				}
			}

			if (ExportPhysicsColliders)
			{
				var colliders = nodeTransform.gameObject.GetComponents<Collider>();
				if (colliders != null)
				{
					ExportColliders(node, colliders);
				}
			}

			return id;
		}

		private void ExportColliders(Node node, Collider[] colliders)
		{
			if (colliders == null) return;

			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(
					new[] { HT_node_colliderExtensionFactory.EXTENSION_NAME }
				);
			}
			else if (!_root.ExtensionsUsed.Contains(HT_node_colliderExtensionFactory.EXTENSION_NAME))
			{
				_root.ExtensionsUsed.Add(HT_node_colliderExtensionFactory.EXTENSION_NAME);
			}

			if (RequireExtensions)
			{
				if (_root.ExtensionsRequired == null)
				{
					_root.ExtensionsRequired = new List<string>(
						new[] { HT_node_colliderExtensionFactory.EXTENSION_NAME }
					);
				}
				else if (!_root.ExtensionsRequired.Contains(HT_node_colliderExtensionFactory.EXTENSION_NAME))
				{
					_root.ExtensionsRequired.Add(HT_node_colliderExtensionFactory.EXTENSION_NAME);
				}
			}

			List<HT_node_colliderExtension.Collider> cs = new List<HT_node_colliderExtension.Collider>();
			foreach (var collider in colliders)
			{
				if (collider.GetType() == typeof(BoxCollider))
				{
					var c = (BoxCollider)collider;
					var center = new Vector3(
						c.center.x * SchemaExtensions.CoordinateSpaceConversionScale.X,
						c.center.y * SchemaExtensions.CoordinateSpaceConversionScale.Y,
						c.center.z * SchemaExtensions.CoordinateSpaceConversionScale.Z);
					cs.Add(new HT_node_colliderExtension.BoxCollider(c.isTrigger,
						new GLTF.Math.Vector3(center.x, center.y, center.z),
						new GLTF.Math.Vector3(c.size.x, c.size.y, c.size.z)));
				}
				else if (collider.GetType() == typeof(SphereCollider))
				{
					var c = (SphereCollider)collider;
					var center = new Vector3(
						c.center.x * SchemaExtensions.CoordinateSpaceConversionScale.X,
						c.center.y * SchemaExtensions.CoordinateSpaceConversionScale.Y,
						c.center.z * SchemaExtensions.CoordinateSpaceConversionScale.Z);
					cs.Add(new HT_node_colliderExtension.SphereCollider(c.isTrigger,
						new GLTF.Math.Vector3(center.x, center.y, center.z),
						c.radius));
				}
				else if (collider.GetType() == typeof(CapsuleCollider))
				{
					var c = (CapsuleCollider)collider;
					var center = new Vector3(
						c.center.x * SchemaExtensions.CoordinateSpaceConversionScale.X,
						c.center.y * SchemaExtensions.CoordinateSpaceConversionScale.Y,
						c.center.z * SchemaExtensions.CoordinateSpaceConversionScale.Z);
					cs.Add(new HT_node_colliderExtension.CapsuleCollider(c.isTrigger,
						new GLTF.Math.Vector3(center.x, center.y, center.z),
						c.radius,
						c.height,
						(HT_node_colliderExtension.CapsuleDirection)c.direction));
				}
			}

			if (node.Extensions == null)
			{
				node.Extensions = new Dictionary<string, IExtension>();
			}

			node.Extensions[HT_node_colliderExtensionFactory.EXTENSION_NAME] = new HT_node_colliderExtension(cs);
		}

		private CameraId ExportCamera(Camera unityCamera)
		{
			GLTFCamera camera = new GLTFCamera();
			//name
			camera.Name = unityCamera.name;

			//type
			bool isOrthographic = unityCamera.orthographic;
			camera.Type = isOrthographic ? CameraType.orthographic : CameraType.perspective;
			Matrix4x4 matrix = unityCamera.projectionMatrix;

			//matrix properties: compute the fields from the projection matrix
			if (isOrthographic)
			{
				CameraOrthographic ortho = new CameraOrthographic();

				ortho.XMag = 1 / matrix[0, 0];
				ortho.YMag = 1 / matrix[1, 1];

				float farClip = (matrix[2, 3] / matrix[2, 2]) - (1 / matrix[2, 2]);
				float nearClip = farClip + (2 / matrix[2, 2]);
				ortho.ZFar = farClip;
				ortho.ZNear = nearClip;

				camera.Orthographic = ortho;
			}
			else
			{
				CameraPerspective perspective = new CameraPerspective();
				float fov = 2 * Mathf.Atan(1 / matrix[1, 1]);
				float aspectRatio = matrix[1, 1] / matrix[0, 0];
				perspective.YFov = fov;
				perspective.AspectRatio = aspectRatio;

				if (matrix[2, 2] == -1)
				{
					//infinite projection matrix
					float nearClip = matrix[2, 3] * -0.5f;
					perspective.ZNear = nearClip;
				}
				else
				{
					//finite projection matrix
					float farClip = matrix[2, 3] / (matrix[2, 2] + 1);
					float nearClip = farClip * (matrix[2, 2] + 1) / (matrix[2, 2] - 1);
					perspective.ZFar = farClip;
					perspective.ZNear = nearClip;
				}
				camera.Perspective = perspective;
			}

			var id = new CameraId
			{
				Id = _root.Cameras.Count,
				Root = _root
			};

			_root.Cameras.Add(camera);

			return id;
		}

		private static bool ContainsValidRenderer (GameObject gameObject)
		{
			var meshRender = gameObject.GetComponent<MeshRenderer>();
			var meshFilter = gameObject.GetComponent<MeshFilter>();
			var skinnedMeshRender = gameObject.GetComponent<SkinnedMeshRenderer>();
			return (meshFilter != null && meshRender != null && meshRender.enabled) || (skinnedMeshRender != null && skinnedMeshRender.enabled);
		}

		private void FilterPrimitives(Transform transform, out GameObject[] meshPrimitives, out GameObject[] skinnedMeshPrimitives, out GameObject[] nonPrimitives)
		{
			var childCount = transform.childCount;
			var meshPrims = new List<GameObject>(childCount + 1);
			var skinnedMeshPrims = new List<GameObject>(childCount + 1);
			var nonPrims = new List<GameObject>(childCount);

			// add another primitive if the root object also has a mesh
			var gameObject = transform.gameObject;
			if (gameObject.activeSelf)
			{
				if (ContainsValidRenderer(gameObject))
				{
					if (gameObject.GetComponent<SkinnedMeshRenderer>() != null)
					{
						skinnedMeshPrims.Add(gameObject);
					}
					else
					{
						meshPrims.Add(gameObject);
					}
				}
			}

			for (var i = 0; i < childCount; i++)
			{
				var go = transform.GetChild(i).gameObject;
				if (IsPrimitive(go))
				{
					if (go.GetComponent<SkinnedMeshRenderer>() != null)
					{
						skinnedMeshPrims.Add(go);
					}
					else
					{
						meshPrims.Add(go);
					}
				}
				else
				{
					nonPrims.Add(go);
				}
			}

			meshPrimitives = meshPrims.ToArray();
			skinnedMeshPrimitives = skinnedMeshPrims.ToArray();
			nonPrimitives = nonPrims.ToArray();
		}

		private static bool IsPrimitive(GameObject gameObject)
		{
			/*
			 * Primitives have the following properties:
			 * - have no children
			 * - have no non-default local transform properties
			 * - have MeshFilter and MeshRenderer components OR has SkinnedMeshRenderer component
			 */
			return gameObject.transform.childCount == 0
				&& gameObject.transform.localPosition == Vector3.zero
				&& gameObject.transform.localRotation == Quaternion.identity
				&& gameObject.transform.localScale == Vector3.one
				&& ContainsValidRenderer(gameObject);
		}
		
		private void ExportSkin(string name, GameObject[] primitives, Node node)
		{
			foreach (var prim in primitives)
			{
				var smr = prim.GetComponent<SkinnedMeshRenderer>();
				if (_existedSkins.TryGetValue(smr.rootBone.GetInstanceID(), out SkinId skinId))
				{
					node.Skin = skinId;
					node.Mesh = ExportMesh(prim.name, new GameObject[] { prim });
					continue;
				}
				
				var skin = new Skin
				{
					Joints = new List<NodeId>()
				};
				
				var mesh = smr.sharedMesh;
				if (mesh.bindposes.Length != 0)
				{
					skin.InverseBindMatrices = ExportAccessor(SchemaExtensions.ConvertMatrix4x4CoordinateSpaceAndCopy(mesh.bindposes));
				}
				var baseId = _root.Nodes.Count;
				
				foreach (var bone in smr.bones)
				{
					var translation = bone.localPosition;
					var rotation = bone.localRotation;
					var scale = bone.localScale;
					
					NodeId nodeId = null;
					if (!_existedNodes.TryGetValue(bone.GetInstanceID(), out nodeId))
					{
						var boneNode = new Node
						{
							Name = bone.gameObject.name,
							Translation = new GLTF.Math.Vector3(translation.x, translation.y, translation.z),
							Rotation = new GLTF.Math.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
							Scale = new GLTF.Math.Vector3(scale.x, scale.y, scale.z),
						};
						
						if (bone.childCount > 0)
						{
							boneNode.Children = new List<NodeId>();
							for (var i = 0; i < bone.childCount; ++i)
							{
								var childIndex = Array.IndexOf(smr.bones, bone.GetChild(i));
								if (-1 == childIndex)
								{
									continue;
								}
								boneNode.Children.Add(
									new NodeId
									{
										Id = childIndex + baseId,
										Root = _root
									}
								);
							}
						}
						
						nodeId = new NodeId
						{
							Id = _root.Nodes.Count,
							Root = _root
						};
						
						_root.Nodes.Add(boneNode);
					}

					skin.Joints.Add(nodeId);
				}

				NodeId rootBoneId = null;
				if (!_existedNodes.TryGetValue(smr.rootBone.GetInstanceID(), out rootBoneId))
				{
					rootBoneId = new NodeId
					{
						Id = baseId,
						Root = _root
					};
				}

				skin.Skeleton = rootBoneId;

				skinId = new SkinId
				{
					Id = _root.Skins.Count,
					Root = _root
				};

				_root.Skins.Add(skin);

				node.Skin = skinId;
				
				node.Mesh = ExportMesh(prim.name, new GameObject[] { prim });

				_existedSkins.Add(smr.rootBone.GetInstanceID(), skinId);
			}
		}

		private MeshId ExportMesh(string name, GameObject[] primitives)
		{
			// check if this set of primitives is already a mesh
			MeshId existingMeshId = null;
			var key = new PrimKey();
			foreach (var prim in primitives)
			{
				var smr = prim.GetComponent<SkinnedMeshRenderer>();
				if (smr != null)
				{
					key.Mesh = smr.sharedMesh;
					key.Material = smr.sharedMaterial;
				}
				else
				{
					var filter = prim.GetComponent<MeshFilter>();
					var renderer = prim.GetComponent<MeshRenderer>();
					key.Mesh = filter.sharedMesh;
					key.Material = renderer.sharedMaterial;
				}

				MeshId tempMeshId;
				if (_primOwner.TryGetValue(key, out tempMeshId) && (existingMeshId == null || tempMeshId == existingMeshId))
				{
					existingMeshId = tempMeshId;
				}
				else
				{
					existingMeshId = null;
					break;
				}
			}

			// if so, return that mesh id
			if (existingMeshId != null)
			{
				return existingMeshId;
			}

			// if not, create new mesh and return its id
			var mesh = new GLTFMesh();

			if (ExportNames)
			{
				mesh.Name = name;
			}

			mesh.Primitives = new List<MeshPrimitive>(primitives.Length);
			foreach (var prim in primitives)
			{
				MeshPrimitive[] meshPrimitives = ExportPrimitive(prim, mesh);
				if (meshPrimitives != null)
				{
					mesh.Primitives.AddRange(meshPrimitives);
				}
			}
			
			var id = new MeshId
			{
				Id = _root.Meshes.Count,
				Root = _root
			};
			_root.Meshes.Add(mesh);

			return id;
		}

		private void ExportAnimations(AnimationClip[] animationClips, Transform rootNodeTransform)
		{
			if (null == animationClips)
			{
				return;
			}
			
			foreach (var animationClip in animationClips)
			{
				var animInstId = animationClip.GetInstanceID();
				AnimationId existedAnimId;
				if (!_existedAnimations.TryGetValue(animInstId, out existedAnimId))
				{
					var animId = ExportAnimationClip(animationClip, rootNodeTransform);
					_existedAnimations.Add(animInstId, animId);
				}
			}
		}
		
		private AnimationId ExportAnimationClip(AnimationClip animationClip, Transform rootNodeTransform)
		{
			var curveBindings = AnimationUtility.GetCurveBindings(animationClip);

			var curveBindingGroups = CurveMarshalling(curveBindings);

			var animation = new GLTFAnimation()
			{
				Name = animationClip.name,
				Channels = new List<AnimationChannel>(),
				Samplers = new List<AnimationSampler>()
			};
			
			var frameCount = Mathf.CeilToInt(animationClip.length * animationClip.frameRate);
			var timestamps = new float[frameCount];
			for (var i = 0; i < frameCount; ++i)
			{
				var timestamp = i / animationClip.frameRate;
				timestamps[i] = timestamp;
			}
			
			var timesstampsId = ExportAccessor(timestamps);
			
			var animationCorrector = new AnimationCorrector();
			animationCorrector.Init(curveBindingGroups, rootNodeTransform);
			foreach (var curveBindingGroup in curveBindingGroups)
			{
				var bone = rootNodeTransform.Find(curveBindingGroup.path);
				if (bone == null)
				{
					continue;
				}
				
				NodeId nodeId = null;
				if (!_existedNodes.TryGetValue(bone.GetInstanceID(), out nodeId))
				{
					continue;
				}

				foreach(var property in curveBindingGroup.properties)
				{
					GLTFAnimationChannelPath path;
					var data = animationCorrector.Corrector(animationClip, property.name, property.curveBindings, frameCount, curveBindingGroup.path, out path);
					AccessorId accessorId = null;
					if (data != null)
					{
						if (path == GLTFAnimationChannelPath.translation)
						{
							accessorId = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(data, SchemaExtensions.CoordinateSpaceConversionScale));
						}
					}

					if (accessorId == null)
					{
						accessorId = ExportCurve(animationClip, property.name, property.curveBindings, frameCount, out path);
					}
					if (accessorId == null)
					{
						continue;
					}
					var sampler = new AnimationSampler()
					{
						Input = timesstampsId,
						Interpolation = InterpolationType.LINEAR,
						Output = accessorId
					};
					
					var samplerId = new SamplerId()
					{
						Id = animation.Samplers.Count,
						Root = _root
					};
					
					animation.Samplers.Add(sampler);
					
					var channel = new AnimationChannel()
					{
						Sampler = samplerId,
						Target = new AnimationChannelTarget()
						{
							Node = nodeId,
							Path = path
						}
					};
					
					animation.Channels.Add(channel);
				}
			}
				
			var id = new AnimationId
			{
				Id = _root.Animations.Count,
				Root = _root
			};
			
			_root.Animations.Add(animation);
			
			return id;
		}
		
		private AccessorId ExportCurve(AnimationClip animationClip, string propertyName, List<EditorCurveBinding> curveBindings, int frameCount, out GLTFAnimationChannelPath path)
		{
			switch (propertyName)
			{
				case "m_LocalPosition":
				{
					var data = new Vector3[frameCount];
					for (var i = 0; i < frameCount; ++i)
					{
						var time = i / animationClip.frameRate;

						var curveIndex = 0;
						var value = new float[3];
						foreach (var curveBinding in curveBindings)
						{
							var curve = AnimationUtility.GetEditorCurve(animationClip, curveBinding);
							if (curve != null) value[curveIndex++] = curve.Evaluate(time);
						}
						
						data[i] = new Vector3(value[0], value[1], value[2]);
					}

					path = GLTFAnimationChannelPath.translation;
					return ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(data, SchemaExtensions.CoordinateSpaceConversionScale));
				}
				case "m_LocalRotation":
				{
					var data = new Quaternion[frameCount];
					for (var i = 0; i < frameCount; ++i)
					{
						var time = i / animationClip.frameRate;

						var curveIndex = 0;
						var value = new float[4];
						foreach (var curveBinding in curveBindings)
						{
							var curve = AnimationUtility.GetEditorCurve(animationClip, curveBinding);
							if (curve != null) value[curveIndex++] = curve.Evaluate(time);
						}
						
						data[i] = new Quaternion(value[0], value[1], value[2], value[3]);
					}

					path = GLTFAnimationChannelPath.rotation;
					return ExportAccessor(SchemaExtensions.ConvertQuaternionCoordinateSpaceAndCopy(data));
				}
				case "m_LocalScale":
				{
					var data = new Vector3[frameCount];
					for (var i = 0; i < frameCount; ++i)
					{
						var time = i / animationClip.frameRate;

						var curveIndex = 0;
						var value = new float[3];
						foreach (var curveBinding in curveBindings)
						{
							var curve = AnimationUtility.GetEditorCurve(animationClip, curveBinding);
							if (curve != null) value[curveIndex++] = curve.Evaluate(time);
						}
						
						data[i] = new Vector3(value[0], value[1], value[2]);
					}

					path = GLTFAnimationChannelPath.scale;
					return ExportAccessor(data);
				}
				case "localEulerAnglesRaw":
				{
					throw new Exception("Parsing of localEulerAnglesRaw is not supported.");
				}
			}

			Debug.LogError("Unrecognized property name: " + propertyName);
			path = GLTFAnimationChannelPath.translation;
			return null;
		}

		private List<CurveBindingGroup> CurveMarshalling(EditorCurveBinding[] curveBindings)
		{
			List<CurveBindingGroup> curveGroups = new List<CurveBindingGroup>();
			foreach (var curveBinding in curveBindings)
			{
				bool bFoundGroup = false;
				foreach (var curveGroup in curveGroups)
				{
					if ((curveGroup.path == curveBinding.path) &&
						(curveGroup.type == curveBinding.type))
					{
						bFoundGroup = true;

						bool bFoundCurveBindingSlot = false;
						foreach (var property in curveGroup.properties)
						{
							if (property.name == Path.GetFileNameWithoutExtension(curveBinding.propertyName))
							{
								property.curveBindings.Add(curveBinding);
								bFoundCurveBindingSlot = true;
							}
						}
						
						if (!bFoundCurveBindingSlot)
						{
							curveGroup.properties.Add(new PropertyCurveBindings(Path.GetFileNameWithoutExtension(curveBinding.propertyName), curveBinding));
						}
					}
				}
				
				if (!bFoundGroup)
				{
					var property = new PropertyCurveBindings(Path.GetFileNameWithoutExtension(curveBinding.propertyName), curveBinding);
					curveGroups.Add(new CurveBindingGroup(curveBinding.path, curveBinding.type, property));
				}
			}

			foreach (var curveGroup in curveGroups)
			{
				curveGroup.properties.Sort((l, r) =>
				{
					Func<string, int> getPriority = (string propertyName) =>
					{
						int priority = -1;
						if (propertyName == "m_LocalPosition")
						{
							priority = 0;
						}
						else if (propertyName == "m_LocalRotation")
						{
							priority = 1;
						}
						else if (propertyName == "localEulerAnglesRaw")
						{
							priority = 2;
						}
						else if (propertyName == "m_LocalScale")
						{
							priority = 3;
						}
						return priority;
					};

					var lp = getPriority(l.name);
					var rp = getPriority(r.name);
					return lp < rp ? -1 : 1;
				});
			}

			return curveGroups;
		}

		// a mesh *might* decode to multiple prims if there are submeshes
		private MeshPrimitive[] ExportPrimitive(GameObject gameObject, GLTFMesh mesh)
		{
			Mesh meshObj = null;
			SkinnedMeshRenderer smr = null;
			var filter = gameObject.GetComponent<MeshFilter>();
			if (filter != null)
			{
				meshObj = filter.sharedMesh;
			}
			else
			{
				smr = gameObject.GetComponent<SkinnedMeshRenderer>();
				meshObj = smr.sharedMesh;
			}
			if (meshObj == null)
			{
				Debug.LogError(string.Format("MeshFilter.sharedMesh on gameobject:{0} is missing , skipping", gameObject.name));
				return null;
			}

			var renderer = gameObject.GetComponent<MeshRenderer>();
			var materialsObj = renderer != null ? renderer.sharedMaterials : smr.sharedMaterials;

			var prims = new MeshPrimitive[meshObj.subMeshCount];

			// don't export any more accessors if this mesh is already exported
			MeshPrimitive[] primVariations;
			if (_meshToPrims.TryGetValue(meshObj, out primVariations)
				&& meshObj.subMeshCount == primVariations.Length)
			{
				for (var i = 0; i < primVariations.Length; i++)
				{
					prims[i] = new MeshPrimitive(primVariations[i], _root)
					{
						Material = ExportMaterial(materialsObj[i], renderer)
					};
				}

				return prims;
			}

			AccessorId aPosition = null, aNormal = null, aTangent = null,
				aTexcoord0 = null, aTexcoord1 = null, aTexcoord2 = null, aTexcoord3 = null,
				aColor0 = null, aJoint0 = null, aWeight0 = null;
			
			aPosition = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.vertices, SchemaExtensions.CoordinateSpaceConversionScale));

			if (meshObj.normals.Length != 0)
				aNormal = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.normals, SchemaExtensions.CoordinateSpaceConversionScale));

			if (meshObj.tangents.Length != 0)
				aTangent = ExportAccessor(SchemaExtensions.ConvertVector4CoordinateSpaceAndCopy(meshObj.tangents, SchemaExtensions.TangentSpaceConversionScale));

			if (meshObj.uv.Length != 0)
				aTexcoord0 = ExportAccessor(meshObj.uv);

			if (meshObj.uv2.Length != 0)
				aTexcoord1 = ExportAccessor(meshObj.uv2);

			if (meshObj.uv3.Length != 0)
				aTexcoord2 = ExportAccessor(meshObj.uv3);

			if (meshObj.uv4.Length != 0)
				aTexcoord3 = ExportAccessor(meshObj.uv4);

			if (meshObj.colors.Length != 0)
				aColor0 = ExportAccessor(meshObj.colors);

			if (meshObj.boneWeights.Length != 0)
			{
				aJoint0 = ExportAccessor(SchemaExtensions.ExtractJointAndCopy(meshObj.boneWeights), SemanticProperties.JOINTS_0);
				aWeight0 = ExportAccessor(SchemaExtensions.ExtractWeightAndCopy(meshObj.boneWeights));
			}

			MaterialId lastMaterialId = null;

			for (var submesh = 0; submesh < meshObj.subMeshCount; submesh++)
			{
				var primitive = new MeshPrimitive();

				var topology = meshObj.GetTopology(submesh);
				var indices = meshObj.GetIndices(submesh);
				if (topology == MeshTopology.Triangles) SchemaExtensions.FlipTriangleFaces(indices);

				primitive.Mode = GetDrawMode(topology);
				primitive.Indices = ExportAccessor(indices, SemanticProperties.INDICES);

				primitive.Attributes = new Dictionary<string, AccessorId>();
				primitive.Attributes.Add(SemanticProperties.POSITION, aPosition);

				if (aNormal != null)
					primitive.Attributes.Add(SemanticProperties.NORMAL, aNormal);
				if (aTangent != null)
					primitive.Attributes.Add(SemanticProperties.TANGENT, aTangent);
				if (aTexcoord0 != null)
					primitive.Attributes.Add(SemanticProperties.TEXCOORD_0, aTexcoord0);
				if (aTexcoord1 != null)
					primitive.Attributes.Add(SemanticProperties.TEXCOORD_1, aTexcoord1);
				if (aTexcoord2 != null)
					primitive.Attributes.Add(SemanticProperties.TEXCOORD_2, aTexcoord2);
				if (aTexcoord3 != null)
					primitive.Attributes.Add(SemanticProperties.TEXCOORD_3, aTexcoord3);
				if (aColor0 != null)
					primitive.Attributes.Add(SemanticProperties.COLOR_0, aColor0);
				if (aJoint0 != null)
					primitive.Attributes.Add(SemanticProperties.JOINTS_0, aJoint0);
				if (aWeight0 != null)
					primitive.Attributes.Add(SemanticProperties.WEIGHTS_0, aWeight0);

				if (submesh < materialsObj.Length)
				{
					primitive.Material = ExportMaterial(materialsObj[submesh], renderer);
					lastMaterialId = primitive.Material;
				}
				else
				{
					primitive.Material = lastMaterialId;
				}

				ExportBlendShapes(smr, meshObj, primitive, mesh);

				prims[submesh] = primitive;
			}

			_meshToPrims[meshObj] = prims;

			return prims;
		}

		private MaterialId ExportMaterial(Material materialObj, MeshRenderer renderer)
		{
			MaterialId id = GetMaterialId(_root, materialObj);
			if (id != null)
			{
				return id;
			}

			var material = new GLTFMaterial();

			if (ExportNames)
			{
				material.Name = materialObj.name;
			}

			if (materialObj.HasProperty("_Cutoff"))
			{
				material.AlphaCutoff = materialObj.GetFloat("_Cutoff");
			}

			switch (materialObj.GetTag("RenderType", false, ""))
			{
				case "TransparentCutout":
					material.AlphaMode = AlphaMode.MASK;
					break;
				case "Transparent":
					material.AlphaMode = AlphaMode.BLEND;
					break;
				default:
					material.AlphaMode = AlphaMode.OPAQUE;
					break;
			}

			material.DoubleSided = materialObj.HasProperty("_Cull") &&
				materialObj.GetInt("_Cull") == (float)CullMode.Off;

			if (materialObj.IsKeywordEnabled("_EMISSION"))
			{ 
				if (materialObj.HasProperty("_EmissionColor"))
				{
					material.EmissiveFactor = materialObj.GetColor("_EmissionColor").ToNumericsColorRaw();
				}

				if (materialObj.HasProperty("_EmissionMap"))
				{
					var emissionTex = materialObj.GetTexture("_EmissionMap");

					if (emissionTex != null)
					{
						if (emissionTex is Texture2D)
						{
							material.EmissiveTexture = ExportTextureInfo(emissionTex, TextureMapType.Emission);
							Vector2 offset = materialObj.GetTextureOffset("_EmissionMap");
							Vector2 scale = materialObj.GetTextureScale("_EmissionMap");
							ExportTextureTransform(material.EmissiveTexture, scale, offset);
						}
						else
						{
							Debug.LogErrorFormat("Can't export a {0} emissive texture in material {1}", emissionTex.GetType(), materialObj.name);
						}

					}
				}
			}
			if (materialObj.HasProperty("_BumpMap") && materialObj.IsKeywordEnabled("_NORMALMAP"))
			{
				var normalTex = materialObj.GetTexture("_BumpMap");

				if (normalTex != null)
				{
					if (normalTex is Texture2D)
					{
						material.NormalTexture = ExportNormalTextureInfo(normalTex, TextureMapType.Bump, materialObj);
						Vector2 offset = materialObj.GetTextureOffset("_BumpMap");
						Vector2 scale = materialObj.GetTextureScale("_BumpMap");
						ExportTextureTransform(material.NormalTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} normal texture in material {1}", normalTex.GetType(), materialObj.name);
					}
				}
			}

			if (materialObj.HasProperty("_OcclusionMap"))
			{
				var occTex = materialObj.GetTexture("_OcclusionMap");
				if (occTex != null)
				{
					if (occTex is Texture2D)
					{
						material.OcclusionTexture = ExportOcclusionTextureInfo(occTex, TextureMapType.Occlusion, materialObj);
						Vector2 offset = materialObj.GetTextureOffset("_OcclusionMap");
						Vector2 scale = materialObj.GetTextureScale("_OcclusionMap");
						ExportTextureTransform(material.OcclusionTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} occlusion texture in material {1}", occTex.GetType(), materialObj.name);
					}
				}
			}

			if (IsPBRMetallicRoughness(materialObj))
			{
				material.PbrMetallicRoughness = ExportPBRMetallicRoughness(materialObj);
			}
			else if (IsPBRSpecularGlossiness(materialObj))
			{
				ExportPBRSpecularGlossiness(material, materialObj);
			}
			else
			{
				if (!ExportCommonMaterial(material, materialObj)) 
				{
					throw new Exception(String.Format("Please check the material of game object {0} and change a valid one.", renderer.transform.name));
				}
			}

			if (HasMaterialsModmap(materialObj, renderer))
			{
				ExportModmap(material, materialObj, renderer);
			}

			_materials.Add(materialObj);

			id = new MaterialId
			{
				Id = _root.Materials.Count,
				Root = _root
			};
			_root.Materials.Add(material);

			return id;
		}

		// Blend Shapes / Morph Targets
		// Adopted from Gary Hsu (bghgary)
		// https://github.com/bghgary/glTF-Tools-for-Unity/blob/master/UnityProject/Assets/Gltf/Editor/Exporter.cs
		private void ExportBlendShapes(SkinnedMeshRenderer smr, Mesh meshObj, MeshPrimitive primitive, GLTFMesh mesh)
		{
			if (smr != null && meshObj.blendShapeCount > 0)
			{
				List<Dictionary<string, AccessorId>> targets = new List<Dictionary<string, AccessorId>>(meshObj.blendShapeCount);
				List<Double> weights = new List<double>(meshObj.blendShapeCount);
				List<string> targetNames = new List<string>(meshObj.blendShapeCount);

				for (int blendShapeIndex = 0; blendShapeIndex < meshObj.blendShapeCount; blendShapeIndex++)
				{

					targetNames.Add(meshObj.GetBlendShapeName(blendShapeIndex));
					// As described above, a blend shape can have multiple frames.  Given that glTF only supports a single frame
					// per blend shape, we'll always use the final frame (the one that would be for when 100% weight is applied).
					int frameIndex = meshObj.GetBlendShapeFrameCount(blendShapeIndex) - 1;

					var deltaVertices = new Vector3[meshObj.vertexCount];
					var deltaNormals = new Vector3[meshObj.vertexCount];
					var deltaTangents = new Vector3[meshObj.vertexCount];
					meshObj.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

					targets.Add(new Dictionary<string, AccessorId>
						{
							{ SemanticProperties.POSITION, ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy( deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale)) },
							{ SemanticProperties.NORMAL, ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaNormals,SchemaExtensions.CoordinateSpaceConversionScale))},
							{ SemanticProperties.TANGENT, ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaTangents, SchemaExtensions.CoordinateSpaceConversionScale)) },
						});

					// We need to get the weight from the SkinnedMeshRenderer because this represents the currently
					// defined weight by the user to apply to this blend shape.  If we instead got the value from
					// the unityMesh, it would be a _per frame_ weight, and for a single-frame blend shape, that would
					// always be 100.  A blend shape might have more than one frame if a user wanted to more tightly
					// control how a blend shape will be animated during weight changes (e.g. maybe they want changes
					// between 0-50% to be really minor, but between 50-100 to be extreme, hence they'd have two frames
					// where the first frame would have a weight of 50 (meaning any weight between 0-50 should be relative
					// to the values in this frame) and then any weight between 50-100 would be relevant to the weights in
					// the second frame.  See Post 20 for more info:
					// https://forum.unity3d.com/threads/is-there-some-method-to-add-blendshape-in-editor.298002/#post-2015679
					weights.Add(smr.GetBlendShapeWeight(blendShapeIndex) / 100);
				}

				mesh.Weights = weights;
				primitive.Targets = targets;
				primitive.TargetNames = targetNames;
			}
		}

		private bool IsPBRMetallicRoughness(Material material)
		{
			return material.HasProperty("_Metallic") && material.HasProperty("_MetallicGlossMap");
		}

		private bool IsPBRSpecularGlossiness(Material material)
		{
			return material.HasProperty("_SpecColor") && material.HasProperty("_SpecGlossMap");
		}

		private bool IsCommonConstantMaterial(Material material)
		{
			return material.HasProperty("_EmissionTex") || material.HasProperty("_EmissionColor");
		}

		private bool IsCommonLambertMaterial(Material material)
		{
			return material.HasProperty("_Ambient") &&
				(material.HasProperty("_MainTex") || material.HasProperty("_DiffuseColor")) &&
				(material.HasProperty("_EmissionTex") || material.HasProperty("_EmissionColor"));
		}

		private bool IsCommonPhongMaterial(Material material)
		{
			return material.HasProperty("_Ambient") &&
				(material.HasProperty("_MainTex") || material.HasProperty("_DiffuseColor")) &&
				(material.HasProperty("_EmissionTex") || material.HasProperty("_EmissionColor")) &&
				(material.HasProperty("_SpecularTex") || material.HasProperty("_SpecularColor")) &&
				material.HasProperty("_Shininess");
		}

		private bool IsCommonBlinnMaterial(Material material)
		{
			return material.HasProperty("_Ambient") &&
				(material.HasProperty("_MainTex") || material.HasProperty("_DiffuseColor")) &&
				(material.HasProperty("_EmissionTex") || material.HasProperty("_EmissionColor")) &&
				(material.HasProperty("_SpecularTex") || material.HasProperty("_SpecularColor")) &&
				material.HasProperty("_Shininess");
		}

		private bool HasMaterialsModmap(Material material, MeshRenderer renderer)
		{
			return ((renderer != null) && (-1 != renderer.lightmapIndex)) || material.HasProperty("_LightMap");
		}

		private void ExportTextureTransform(TextureInfo def, Vector2 scale, Vector2 offset)
		{
			if (offset == Vector2.zero && scale == Vector2.one) return;

			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(
					new[] { ExtTextureTransformExtensionFactory.EXTENSION_NAME }
				);
			}
			else if (!_root.ExtensionsUsed.Contains(ExtTextureTransformExtensionFactory.EXTENSION_NAME))
			{
				_root.ExtensionsUsed.Add(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
			}

			if (RequireExtensions)
			{
				if (_root.ExtensionsRequired == null)
				{
					_root.ExtensionsRequired = new List<string>(
						new[] { ExtTextureTransformExtensionFactory.EXTENSION_NAME }
					);
				}
				else if (!_root.ExtensionsRequired.Contains(ExtTextureTransformExtensionFactory.EXTENSION_NAME))
				{
					_root.ExtensionsRequired.Add(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
				}
			}

			if (def.Extensions == null)
				def.Extensions = new Dictionary<string, IExtension>();

			def.Extensions[ExtTextureTransformExtensionFactory.EXTENSION_NAME] = new ExtTextureTransformExtension(
				new GLTF.Math.Vector2(offset.x, offset.y),
				0, // TODO: support rotation
				new GLTF.Math.Vector2(scale.x, scale.y),
				0 // TODO: support UV channels
			);
		}

		private NormalTextureInfo ExportNormalTextureInfo(
			Texture texture,
			TextureMapType textureMapType,
			Material material)
		{
			var info = new NormalTextureInfo();

			info.Index = ExportTexture(texture, textureMapType);

			if (material.HasProperty("_BumpScale"))
			{
				info.Scale = material.GetFloat("_BumpScale");
			}

			return info;
		}

		private OcclusionTextureInfo ExportOcclusionTextureInfo(
			Texture texture,
			TextureMapType textureMapType,
			Material material)
		{
			var info = new OcclusionTextureInfo();

			info.Index = ExportTexture(texture, textureMapType);

			if (material.HasProperty("_OcclusionStrength"))
			{
				info.Strength = material.GetFloat("_OcclusionStrength");
			}

			return info;
		}

		private PbrMetallicRoughness ExportPBRMetallicRoughness(Material material)
		{
			var pbr = new PbrMetallicRoughness();

			if (material.HasProperty("_Color"))
			{
				pbr.BaseColorFactor = material.GetColor("_Color").ToNumericsColorRaw();
			}

			if (material.HasProperty("_MainTex"))
			{
				var mainTex = material.GetTexture("_MainTex");

				if (mainTex != null)
				{
					if (mainTex is Texture2D)
					{
						pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.Main);
						Vector2 offset = material.GetTextureOffset("_MainTex");
						Vector2 scale = material.GetTextureScale("_MainTex");
						ExportTextureTransform(pbr.BaseColorTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} base texture in material {1}", mainTex.GetType(), material.name);
					}
				}
			}

			if (material.HasProperty("_Metallic"))
			{
				var metallicGlossMap = material.GetTexture("_MetallicGlossMap");
				pbr.MetallicFactor = (metallicGlossMap != null) ? 1.0 : material.GetFloat("_Metallic");
			}

			if (material.HasProperty("_Glossiness"))
			{
				var metallicGlossMap = material.GetTexture("_MetallicGlossMap");
				pbr.RoughnessFactor = (metallicGlossMap != null) ? 1.0 : 1.0 - material.GetFloat("_Glossiness");
			}

			if (material.HasProperty("_MetallicGlossMap"))
			{
				var mrTex = material.GetTexture("_MetallicGlossMap");

				if (mrTex != null)
				{
					if (mrTex is Texture2D)
					{
						pbr.MetallicRoughnessTexture = ExportTextureInfo(mrTex, TextureMapType.MetallicGloss);
						Vector2 offset = material.GetTextureOffset("_MetallicGlossMap");
						Vector2 scale = material.GetTextureScale("_MetallicGlossMap");
						ExportTextureTransform(pbr.MetallicRoughnessTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} metallic smoothness texture in material {1}", mrTex.GetType(), material.name);
					}
				}
			}
			return pbr;
		}

		private void ExportPBRSpecularGlossiness(GLTFMaterial material, Material materialObj)
		{
			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(new[] { "KHR_materials_pbrSpecularGlossiness" });
			}
			else if (!_root.ExtensionsUsed.Contains("KHR_materials_pbrSpecularGlossiness"))
			{
				_root.ExtensionsUsed.Add("KHR_materials_pbrSpecularGlossiness");
			}
			if (RequireExtensions)
			{
				if (_root.ExtensionsRequired == null)
				{
					_root.ExtensionsRequired = new List<string>(new[] { "KHR_materials_pbrSpecularGlossiness" });
				}
				else if (!_root.ExtensionsRequired.Contains("KHR_materials_pbrSpecularGlossiness"))
				{
					_root.ExtensionsRequired.Add("KHR_materials_pbrSpecularGlossiness");
				}
			}
			if (material.Extensions == null)
			{
				material.Extensions = new Dictionary<string, IExtension>();
			}

			GLTF.Math.Color diffuseFactor = KHR_materials_pbrSpecularGlossinessExtension.DIFFUSE_FACTOR_DEFAULT;
			TextureInfo diffuseTexture = KHR_materials_pbrSpecularGlossinessExtension.DIFFUSE_TEXTURE_DEFAULT;
			GLTF.Math.Vector3 specularFactor = KHR_materials_pbrSpecularGlossinessExtension.SPEC_FACTOR_DEFAULT;
			double glossinessFactor = KHR_materials_pbrSpecularGlossinessExtension.GLOSS_FACTOR_DEFAULT;
			TextureInfo specularGlossinessTexture = KHR_materials_pbrSpecularGlossinessExtension.SPECULAR_GLOSSINESS_TEXTURE_DEFAULT;

			if (materialObj.HasProperty("_Color"))
			{
				diffuseFactor = materialObj.GetColor("_Color").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_MainTex"))
			{
				var mainTex = materialObj.GetTexture("_MainTex");
				if (mainTex != null)
				{
					if (mainTex is Texture2D)
					{
						diffuseTexture = ExportTextureInfo(mainTex, TextureMapType.Main);
						Vector2 offset = materialObj.GetTextureOffset("_MainTex");
						Vector2 scale = materialObj.GetTextureScale("_MainTex");
						ExportTextureTransform(diffuseTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} diffuse texture in material {1}", mainTex.GetType(), materialObj.name);
					}
				}
			}

			if (materialObj.HasProperty("_SpecColor"))
			{
				var specGlossMap = materialObj.GetTexture("_SpecGlossMap");
				if (specGlossMap == null)
				{
					Color specColor = materialObj.GetColor("_SpecColor");
					specularFactor = new GLTF.Math.Vector3(specColor.r, specColor.g, specColor.b);
				}
			}

			if (materialObj.HasProperty("_Glossiness"))
			{
				var specGlossMap = materialObj.GetTexture("_SpecGlossMap");
				if (specGlossMap == null)
				{
					glossinessFactor = materialObj.GetFloat("_Glossiness");
				}
			}

			if (materialObj.HasProperty("_SpecGlossMap"))
			{
				var mgTex = materialObj.GetTexture("_SpecGlossMap");

				if (mgTex != null)
				{
					if (mgTex is Texture2D)
					{
						specularGlossinessTexture = ExportTextureInfo(mgTex, TextureMapType.SpecGloss);
						Vector2 offset = materialObj.GetTextureOffset("_SpecGlossMap");
						Vector2 scale = materialObj.GetTextureScale("_SpecGlossMap");
						ExportTextureTransform(specularGlossinessTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} specular glossiness texture in material {1}", mgTex.GetType(), materialObj.name);
					}
				}
			}

			material.Extensions[KHR_materials_pbrSpecularGlossinessExtensionFactory.EXTENSION_NAME] = new KHR_materials_pbrSpecularGlossinessExtension(
				diffuseFactor,
				diffuseTexture,
				specularFactor,
				glossinessFactor,
				specularGlossinessTexture
			);
		}

		private bool ExportCommonMaterial(GLTFMaterial material, Material materialObj)
		{
			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(new[] { "KHR_materials_common" });
			}
			else if (!_root.ExtensionsUsed.Contains("KHR_materials_common"))
			{
				_root.ExtensionsUsed.Add("KHR_materials_common");
			}

			if (RequireExtensions)
			{
				if (_root.ExtensionsRequired == null)
				{
					_root.ExtensionsRequired = new List<string>(new[] { "KHR_materials_common" });
				}
				else if (!_root.ExtensionsRequired.Contains("KHR_materials_common"))
				{
					_root.ExtensionsRequired.Add("KHR_materials_common");
				}
			}

			if (material.Extensions == null)
			{
				material.Extensions = new Dictionary<string, IExtension>();
			}

			KHR_materials_commonExtension.CommonTechnique technique = KHR_materials_commonExtension.TECHNIQUE_DEFAULT;
			GLTF.Math.Color ambient = KHR_materials_commonExtension.AMBIENT_DEFAULT;
			GLTF.Math.Color emissionColor = KHR_materials_commonExtension.EMISSIONCOLOR_DEFAULT;
			TextureInfo emissionTexture = KHR_materials_commonExtension.EMISSIONTEXTURE_DEFAULT;
			GLTF.Math.Color diffuseColor = KHR_materials_commonExtension.DIFFUSECOLOR_DEFAULT;
			TextureInfo diffuseTexture = KHR_materials_commonExtension.DIFFUSETEXTURE_DEFAULT;
			GLTF.Math.Color specularColor = KHR_materials_commonExtension.SPECULARCOLOR_DEFAULT;
			TextureInfo specularTexture = KHR_materials_commonExtension.SPECULARTEXTURE_DEFAULT;
			float shininess = KHR_materials_commonExtension.SHININESS_DEFAULT;
			float transparency = KHR_materials_commonExtension.TRANSPARENCY_DEFAULT;
			bool transparent = material.AlphaMode == AlphaMode.OPAQUE ? false : true;
			bool doubleSided = material.DoubleSided;

			if (materialObj.HasProperty("_Ambient"))
			{
				ambient = materialObj.GetColor("_Ambient").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_EmissionTex"))
			{
				var emissionTex = materialObj.GetTexture("_EmissionTex");

				if (emissionTex != null)
				{
					if (emissionTex is Texture2D)
					{
						emissionTexture = ExportTextureInfo(emissionTex, TextureMapType.Emission);
						Vector2 offset = materialObj.GetTextureOffset("_EmissionTex");
						Vector2 scale = materialObj.GetTextureScale("_EmissionTex");
						ExportTextureTransform(emissionTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} emission texture in material {1}", emissionTex.GetType(), materialObj.name);
					}
				}
			}
			else if (materialObj.HasProperty("_EmissionColor"))
			{
				emissionColor = materialObj.GetColor("_EmissionColor").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_MainTex"))
			{
				var mainTex = materialObj.GetTexture("_MainTex");

				if (mainTex != null)
				{
					if (mainTex is Texture2D)
					{
						diffuseTexture = ExportTextureInfo(mainTex, TextureMapType.Main);
						Vector2 offset = materialObj.GetTextureOffset("_MainTex");
						Vector2 scale = materialObj.GetTextureScale("_MainTex");
						ExportTextureTransform(diffuseTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} base texture in material {1}", mainTex.GetType(), materialObj.name);
					}
				}
			}
			else if (materialObj.HasProperty("_DiffuseColor"))
			{
				diffuseColor = materialObj.GetColor("_DiffuseColor").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_SpecularTex"))
			{
				var specTex = materialObj.GetTexture("_SpecularTex");

				if (specTex != null)
				{
					if (specTex is Texture2D)
					{
						specularTexture = ExportTextureInfo(specTex, TextureMapType.SpecGloss);
						Vector2 offset = materialObj.GetTextureOffset("_SpecularTex");
						Vector2 scale = materialObj.GetTextureScale("_SpecularTex");
						ExportTextureTransform(specularTexture, scale, offset);
					}
					else
					{
						Debug.LogErrorFormat("Can't export a {0} specular texture in material {1}", specularTexture.GetType(), materialObj.name);
					}
				}
			}
			else if (materialObj.HasProperty("_SpecularColor"))
			{
				specularColor = materialObj.GetColor("_SpecularColor").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_Shininess"))
			{
				shininess = materialObj.GetFloat("_Shininess");
			}

			if (materialObj.HasProperty("_Transparency"))
			{
				transparency = materialObj.GetFloat("_Transparency");
			}

			if (IsCommonBlinnMaterial(materialObj))
			{
				technique = KHR_materials_commonExtension.CommonTechnique.PHONG;
			}
			else if (IsCommonPhongMaterial(materialObj))
			{
				technique = KHR_materials_commonExtension.CommonTechnique.BLINN;
			}
			else if (IsCommonLambertMaterial(materialObj))
			{
				technique = KHR_materials_commonExtension.CommonTechnique.LAMBERT;
			}
			else if (IsCommonConstantMaterial(materialObj))
			{
				technique = KHR_materials_commonExtension.CommonTechnique.CONSTANT;
			}

			if (technique == KHR_materials_commonExtension.CommonTechnique.NONE)
			{
				return false;
			}

			material.Extensions[KHR_materials_commonExtensionFactory.EXTENSION_NAME] = new KHR_materials_commonExtension(
				technique,
				ambient,
				emissionColor,
				emissionTexture,
				diffuseColor,
				diffuseTexture,
				specularColor,
				specularTexture,
				shininess,
				transparency,
				transparent,
				doubleSided
			);

			return true;
		}
		
		private void ExportModmap(GLTFMaterial material, Material materialObj, MeshRenderer renderer)
		{
			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(new[] { "FB_materials_modmap" });
			}
			else if (!_root.ExtensionsUsed.Contains("FB_materials_modmap"))
			{
				_root.ExtensionsUsed.Add("FB_materials_modmap");
			}

			if (RequireExtensions)
			{
				if (_root.ExtensionsRequired == null)
				{
					_root.ExtensionsRequired = new List<string>(new[] { "FB_materials_modmap" });
				}
				else if (!_root.ExtensionsRequired.Contains("FB_materials_modmap"))
				{
					_root.ExtensionsRequired.Add("FB_materials_modmap");
				}
			}

			if (material.Extensions == null)
			{
				material.Extensions = new Dictionary<string, IExtension>();
			}

			GLTF.Math.Vector3 modmapFactor = FB_materials_modmapExtension.MODMAP_FACTOR_DEFAULT;
			TextureInfo modmapTexture = FB_materials_modmapExtension.MODMAP_TEXTURE_DEFAULT;

			if (materialObj.HasProperty("_LightFactor"))
			{
				Color color = materialObj.GetColor("_LightFactor");
				modmapFactor = new GLTF.Math.Vector3(color.r, color.g, color.b);
			}

			Texture lmTex = null;

			if (materialObj.HasProperty("_LightMap"))
			{
				lmTex = materialObj.GetTexture("_LightMap");

				if (lmTex != null)
				{
					modmapTexture = ExportTextureInfo(lmTex, TextureMapType.Light);
					Vector2 offset = materialObj.GetTextureOffset("_LightMap");
					Vector2 scale = materialObj.GetTextureScale("_LightMap");
					ExportTextureTransform(modmapTexture, scale, offset);
				}
			}

			if (lmTex == null)
			{
				if (null != LightmapSettings.lightmaps &&
					LightmapSettings.lightmaps.Length > 0 &&
					renderer.lightmapIndex < LightmapSettings.lightmaps.Length)
				{
					var lightmapData = LightmapSettings.lightmaps[renderer.lightmapIndex];
					lmTex = lightmapData.lightmapColor;
					if (lmTex != null)
					{
						modmapTexture = ExportTextureInfo(lmTex, TextureMapType.Light);
						var lmScaleOffset = renderer.lightmapScaleOffset;
						ExportTextureTransform(modmapTexture, new Vector2(lmScaleOffset.x, lmScaleOffset.y), new Vector2(lmScaleOffset.z, lmScaleOffset.w));
					}
				}
			}

			material.Extensions[FB_materials_modmapExtensionFactory.EXTENSION_NAME] = new FB_materials_modmapExtension(
				modmapFactor,
				modmapTexture
			);
		}

		private TextureInfo ExportTextureInfo(Texture texture, TextureMapType textureMapType)
		{
			var info = new TextureInfo();

			info.Index = ExportTexture(texture, textureMapType);

			return info;
		}

		private TextureId ExportTexture(Texture textureObj, TextureMapType textureMapType)
		{
			TextureId id = GetTextureId(_root, textureObj);
			if (id != null)
			{
				return id;
			}

			var texture = new GLTFTexture();

			//If texture name not set give it a unique name using count
			if (textureObj.name == "")
			{
				textureObj.name = (_root.Textures.Count + 1).ToString();
			}

			if (ExportNames)
			{
				texture.Name = textureObj.name;
			}

			if (_shouldUseInternalBufferForImages)
			{
				texture.Source = ExportImageInternalBuffer(textureObj, textureMapType);
			}
			else
			{
				texture.Source = ExportImage(textureObj, textureMapType);
			}
			texture.Sampler = ExportSampler(textureObj);

			_textures.Add(textureObj);

			id = new TextureId
			{
				Id = _root.Textures.Count,
				Root = _root
			};

			_root.Textures.Add(texture);

			return id;
		}

		private ImageId ExportImage(Texture texture, TextureMapType texturMapType)
		{
			ImageId id = GetImageId(_root, texture);
			if (id != null)
			{
				return id;
			}

			var image = new GLTFImage();

			if (ExportNames)
			{
				image.Name = texture.name;
			}

			_imageInfos.Add(new ImageInfo
			{
				texture = texture as Texture2D,
				textureMapType = texturMapType
			});

			var imagePath = _exportOptions.TexturePathRetriever(texture);
			if (string.IsNullOrEmpty(imagePath))
			{
				imagePath = texture.name;
			}

			var filenamePath = Path.ChangeExtension(imagePath, ".png");
			if (!ExportFullPath)
			{
				filenamePath = Path.ChangeExtension(texture.name, ".png");
			}
			image.Uri = Uri.EscapeUriString(filenamePath);

			id = new ImageId
			{
				Id = _root.Images.Count,
				Root = _root
			};

			_root.Images.Add(image);

			return id;
		}

		private ImageId ExportImageInternalBuffer(UnityEngine.Texture texture, TextureMapType texturMapType)
		{
		    if (texture == null)
		    {
				throw new Exception("texture can not be NULL.");
		    }

			var flipped = new Texture2D(texture.width, texture.height);
			texture = FlipTexture(texture, flipped, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

			var image = new GLTFImage();
		    image.MimeType = "image/png";

		    var byteOffset = _bufferWriter.BaseStream.Position;

		    {//
			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			GL.sRGBWrite = true;
			switch (texturMapType)
			{
			    case TextureMapType.MetallicGloss:
				Graphics.Blit(texture, destRenderTexture, _metalGlossChannelSwapMaterial);
				break;
			    case TextureMapType.Bump:
				Graphics.Blit(texture, destRenderTexture, _normalChannelMaterial);
				break;
			    default:
				Graphics.Blit(texture, destRenderTexture);
				break;
			}

			var exportTexture = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
			exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
			exportTexture.Apply();

			var pngImageData = exportTexture.EncodeToPNG();
			_bufferWriter.Write(pngImageData);

			RenderTexture.ReleaseTemporary(destRenderTexture);

			GL.sRGBWrite = false;
			if (Application.isEditor)
			{
			    UnityEngine.Object.DestroyImmediate(exportTexture);
				UnityEngine.Object.DestroyImmediate(flipped);
			}
			else
			{
			    UnityEngine.Object.Destroy(exportTexture);
				UnityEngine.Object.Destroy(flipped);
			}
		    }

		    var byteLength = _bufferWriter.BaseStream.Position - byteOffset;

		    byteLength = AppendToBufferMultiplyOf4(byteOffset, byteLength);

		    image.BufferView = ExportBufferView((uint)byteOffset, (uint)byteLength);


		    var id = new ImageId
		    {
			Id = _root.Images.Count,
			Root = _root
		    };
		    _root.Images.Add(image);

		    return id;
		}
		private SamplerId ExportSampler(Texture texture)
		{
			var samplerId = GetSamplerId(_root, texture);
			if (samplerId != null)
				return samplerId;

			var sampler = new Sampler();

			switch (texture.wrapMode)
			{
				case TextureWrapMode.Clamp:
					sampler.WrapS = WrapMode.ClampToEdge;
					sampler.WrapT = WrapMode.ClampToEdge;
					break;
				case TextureWrapMode.Repeat:
					sampler.WrapS = WrapMode.Repeat;
					sampler.WrapT = WrapMode.Repeat;
					break;
				case TextureWrapMode.Mirror:
					sampler.WrapS = WrapMode.MirroredRepeat;
					sampler.WrapT = WrapMode.MirroredRepeat;
					break;
				default:
					Debug.LogWarning("Unsupported Texture.wrapMode: " + texture.wrapMode);
					sampler.WrapS = WrapMode.Repeat;
					sampler.WrapT = WrapMode.Repeat;
					break;
			}

			switch (texture.filterMode)
			{
				case FilterMode.Point:
					sampler.MinFilter = MinFilterMode.NearestMipmapNearest;
					sampler.MagFilter = MagFilterMode.Nearest;
					break;
				case FilterMode.Bilinear:
					sampler.MinFilter = MinFilterMode.LinearMipmapNearest;
					sampler.MagFilter = MagFilterMode.Linear;
					break;
				case FilterMode.Trilinear:
					sampler.MinFilter = MinFilterMode.LinearMipmapLinear;
					sampler.MagFilter = MagFilterMode.Linear;
					break;
				default:
					Debug.LogWarning("Unsupported Texture.filterMode: " + texture.filterMode);
					sampler.MinFilter = MinFilterMode.LinearMipmapLinear;
					sampler.MagFilter = MagFilterMode.Linear;
					break;
			}

			samplerId = new SamplerId
			{
				Id = _root.Samplers.Count,
				Root = _root
			};

			_root.Samplers.Add(sampler);

			return samplerId;
		}

		private AccessorId ExportAccessor(float[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.SCALAR;

			var min = arr[0];
			var max = arr[0];

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur < min)
				{
					min = cur;
				}
				if (cur > max)
				{
					max = cur;
				}
			}

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);
			
			accessor.ComponentType = GLTFComponentType.Float;
			
			foreach (var v in arr)
			{
				_bufferWriter.Write(v);
			}
			
			accessor.Min = new List<double> { min };
			accessor.Max = new List<double> { max };

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);
			
			return id;
		}
		
		private AccessorId ExportAccessor(int[] arr, string property)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			bool isJoints = property == SemanticProperties.JOINTS_0;
			bool isIndices = property == SemanticProperties.INDICES;

			var accessor = new Accessor();
			accessor.Count = isJoints ? count / 4 : count;
			accessor.Type = isJoints ? GLTFAccessorAttributeType.VEC4 : GLTFAccessorAttributeType.SCALAR;

			int min = arr[0];
			int max = arr[0];

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur < min)
				{
					min = cur;
				}
				if (cur > max)
				{
					max = cur;
				}
			}

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			if (max <= byte.MaxValue && min >= byte.MinValue)
			{
				accessor.ComponentType = GLTFComponentType.UnsignedByte;

				foreach (var v in arr)
				{
					_bufferWriter.Write((byte)v);
				}
			}
			else if (max <= sbyte.MaxValue && min >= sbyte.MinValue && !isIndices)
			{
				accessor.ComponentType = GLTFComponentType.Byte;

				foreach (var v in arr)
				{
					_bufferWriter.Write((sbyte)v);
				}
			}
			else if (max <= short.MaxValue && min >= short.MinValue && !isIndices)
			{
				accessor.ComponentType = GLTFComponentType.Short;

				foreach (var v in arr)
				{
					_bufferWriter.Write((short)v);
				}
			}
			else if (max <= ushort.MaxValue && min >= ushort.MinValue)
			{
				accessor.ComponentType = GLTFComponentType.UnsignedShort;

				foreach (var v in arr)
				{
					_bufferWriter.Write((ushort)v);
				}
			}
			else if (min >= uint.MinValue)
			{
				accessor.ComponentType = GLTFComponentType.UnsignedInt;

				foreach (var v in arr)
				{
					_bufferWriter.Write((uint)v);
				}
			}
			else
			{
				accessor.ComponentType = GLTFComponentType.Float;

				foreach (var v in arr)
				{
					_bufferWriter.Write((float)v);
				}
			}

			if (accessor.Type == GLTFAccessorAttributeType.SCALAR)
			{
				accessor.Min = new List<double> { min };
				accessor.Max = new List<double> { max };
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private long AppendToBufferMultiplyOf4(long byteOffset, long byteLength)
		{
		    var moduloOffset = byteLength % 4;
		    if (moduloOffset > 0)
		    {
			for (int i = 0; i < (4 - moduloOffset); i++)
			{
			    _bufferWriter.Write((byte)0x00);
			}
			byteLength = _bufferWriter.BaseStream.Position - byteOffset;
		    }

		    return byteLength;
		}

		private AccessorId ExportAccessor(Vector2[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.VEC2;

			float minX = arr[0].x;
			float minY = arr[0].y;
			float maxX = arr[0].x;
			float maxY = arr[0].y;

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur.x < minX)
				{
					minX = cur.x;
				}
				if (cur.y < minY)
				{
					minY = cur.y;
				}
				if (cur.x > maxX)
				{
					maxX = cur.x;
				}
				if (cur.y > maxY)
				{
					maxY = cur.y;
				}
			}

			accessor.Min = new List<double> { minX, minY };
			accessor.Max = new List<double> { maxX, maxY };

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			foreach (var vec in arr)
			{
				_bufferWriter.Write(vec.x);
				_bufferWriter.Write(vec.y);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private AccessorId ExportAccessor(Vector3[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.VEC3;

			float minX = arr[0].x;
			float minY = arr[0].y;
			float minZ = arr[0].z;
			float maxX = arr[0].x;
			float maxY = arr[0].y;
			float maxZ = arr[0].z;

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur.x < minX)
				{
					minX = cur.x;
				}
				if (cur.y < minY)
				{
					minY = cur.y;
				}
				if (cur.z < minZ)
				{
					minZ = cur.z;
				}
				if (cur.x > maxX)
				{
					maxX = cur.x;
				}
				if (cur.y > maxY)
				{
					maxY = cur.y;
				}
				if (cur.z > maxZ)
				{
					maxZ = cur.z;
				}
			}

			accessor.Min = new List<double> { minX, minY, minZ };
			accessor.Max = new List<double> { maxX, maxY, maxZ };

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

				foreach (var vec in arr)
				{
					_bufferWriter.Write(vec.x);
					_bufferWriter.Write(vec.y);
					_bufferWriter.Write(vec.z);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private AccessorId ExportAccessor(Vector4[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.VEC4;

			float minX = arr[0].x;
			float minY = arr[0].y;
			float minZ = arr[0].z;
			float minW = arr[0].w;
			float maxX = arr[0].x;
			float maxY = arr[0].y;
			float maxZ = arr[0].z;
			float maxW = arr[0].w;

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur.x < minX)
				{
					minX = cur.x;
				}
				if (cur.y < minY)
				{
					minY = cur.y;
				}
				if (cur.z < minZ)
				{
					minZ = cur.z;
				}
				if (cur.w < minW)
				{
					minW = cur.w;
				}
				if (cur.x > maxX)
				{
					maxX = cur.x;
				}
				if (cur.y > maxY)
				{
					maxY = cur.y;
				}
				if (cur.z > maxZ)
				{
					maxZ = cur.z;
				}
				if (cur.w > maxW)
				{
					maxW = cur.w;
				}
			}

			accessor.Min = new List<double> { minX, minY, minZ, minW };
			accessor.Max = new List<double> { maxX, maxY, maxZ, maxW };

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			foreach (var vec in arr)
			{
					_bufferWriter.Write(vec.x);
					_bufferWriter.Write(vec.y);
					_bufferWriter.Write(vec.z);
					_bufferWriter.Write(vec.w);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private AccessorId ExportAccessor(Quaternion[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.VEC4;

			float minX = arr[0].x;
			float minY = arr[0].y;
			float minZ = arr[0].z;
			float minW = arr[0].w;
			float maxX = arr[0].x;
			float maxY = arr[0].y;
			float maxZ = arr[0].z;
			float maxW = arr[0].w;

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur.x < minX)
				{
					minX = cur.x;
				}
				if (cur.y < minY)
				{
					minY = cur.y;
				}
				if (cur.z < minZ)
				{
					minZ = cur.z;
				}
				if (cur.w < minW)
				{
					minW = cur.w;
				}
				if (cur.x > maxX)
				{
					maxX = cur.x;
				}
				if (cur.y > maxY)
				{
					maxY = cur.y;
				}
				if (cur.z > maxZ)
				{
					maxZ = cur.z;
				}
				if (cur.w > maxW)
				{
					maxW = cur.w;
				}
			}

			accessor.Min = new List<double> { minX, minY, minZ, minW };
			accessor.Max = new List<double> { maxX, maxY, maxZ, maxW };

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			foreach (var vec in arr)
			{
					_bufferWriter.Write(vec.x);
					_bufferWriter.Write(vec.y);
					_bufferWriter.Write(vec.z);
					_bufferWriter.Write(vec.w);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private AccessorId ExportAccessor(Color[] arr)
		{
			uint count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.VEC4;

			float minR = arr[0].r;
			float minG = arr[0].g;
			float minB = arr[0].b;
			float minA = arr[0].a;
			float maxR = arr[0].r;
			float maxG = arr[0].g;
			float maxB = arr[0].b;
			float maxA = arr[0].a;

			for (var i = 1; i < count; i++)
			{
				var cur = arr[i];

				if (cur.r < minR)
				{
					minR = cur.r;
				}
				if (cur.g < minG)
				{
					minG = cur.g;
				}
				if (cur.b < minB)
				{
					minB = cur.b;
				}
				if (cur.a < minA)
				{
					minA = cur.a;
				}
				if (cur.r > maxR)
				{
					maxR = cur.r;
				}
				if (cur.g > maxG)
				{
					maxG = cur.g;
				}
				if (cur.b > maxB)
				{
					maxB = cur.b;
				}
				if (cur.a > maxA)
				{
					maxA = cur.a;
				}
			}

			accessor.Min = new List<double> { minR, minG, minB, minA };
			accessor.Max = new List<double> { maxR, maxG, maxB, maxA };

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			foreach (var color in arr)
			{
					_bufferWriter.Write(color.r);
					_bufferWriter.Write(color.g);
					_bufferWriter.Write(color.b);
					_bufferWriter.Write(color.a);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private AccessorId ExportAccessor(Matrix4x4[] arr)
		{
			var count = (uint)arr.Length;

			if (count == 0)
			{
				throw new Exception("Accessors can not have a count of 0.");
			}

			var accessor = new Accessor();
			accessor.ComponentType = GLTFComponentType.Float;
			accessor.Count = count;
			accessor.Type = GLTFAccessorAttributeType.MAT4;

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			foreach (var mat in arr)
			{
				_bufferWriter.Write(mat.m00);
				_bufferWriter.Write(mat.m10);
				_bufferWriter.Write(mat.m20);
				_bufferWriter.Write(mat.m30);
				_bufferWriter.Write(mat.m01);
				_bufferWriter.Write(mat.m11);
				_bufferWriter.Write(mat.m21);
				_bufferWriter.Write(mat.m31);
				_bufferWriter.Write(mat.m02);
				_bufferWriter.Write(mat.m12);
				_bufferWriter.Write(mat.m22);
				_bufferWriter.Write(mat.m32);
				_bufferWriter.Write(mat.m03);
				_bufferWriter.Write(mat.m13);
				_bufferWriter.Write(mat.m23);
				_bufferWriter.Write(mat.m33);
			}

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);

			accessor.BufferView = ExportBufferView(byteOffset, byteLength);

			var id = new AccessorId
			{
				Id = _root.Accessors.Count,
				Root = _root
			};
			_root.Accessors.Add(accessor);

			return id;
		}

		private BufferViewId ExportBufferView(uint byteOffset, uint byteLength)
		{
			var bufferView = new BufferView
			{
				Buffer = _bufferId,
				ByteOffset = byteOffset,
				ByteLength = byteLength
			};

			var id = new BufferViewId
			{
				Id = _root.BufferViews.Count,
				Root = _root
			};

			_root.BufferViews.Add(bufferView);

			return id;
		}

		public MaterialId GetMaterialId(GLTFRoot root, Material materialObj)
		{
			for (var i = 0; i < _materials.Count; i++)
			{
				if (_materials[i] == materialObj)
				{
					return new MaterialId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public TextureId GetTextureId(GLTFRoot root, Texture textureObj)
		{
			for (var i = 0; i < _textures.Count; i++)
			{
				if (_textures[i] == textureObj)
				{
					return new TextureId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public ImageId GetImageId(GLTFRoot root, Texture imageObj)
		{
			for (var i = 0; i < _imageInfos.Count; i++)
			{
				if (_imageInfos[i].texture == imageObj)
				{
					return new ImageId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public SamplerId GetSamplerId(GLTFRoot root, Texture textureObj)
		{
			for (var i = 0; i < root.Samplers.Count; i++)
			{
				bool filterIsNearest = root.Samplers[i].MinFilter == MinFilterMode.Nearest
					|| root.Samplers[i].MinFilter == MinFilterMode.NearestMipmapNearest
					|| root.Samplers[i].MinFilter == MinFilterMode.NearestMipmapLinear;

				bool filterIsLinear = root.Samplers[i].MinFilter == MinFilterMode.Linear
					|| root.Samplers[i].MinFilter == MinFilterMode.LinearMipmapNearest;

				bool filterMatched = textureObj.filterMode == FilterMode.Point && filterIsNearest
					|| textureObj.filterMode == FilterMode.Bilinear && filterIsLinear
					|| textureObj.filterMode == FilterMode.Trilinear && root.Samplers[i].MinFilter == MinFilterMode.LinearMipmapLinear;

				bool wrapMatched = textureObj.wrapMode == TextureWrapMode.Clamp && root.Samplers[i].WrapS == WrapMode.ClampToEdge
					|| textureObj.wrapMode == TextureWrapMode.Repeat && root.Samplers[i].WrapS == WrapMode.Repeat
					|| textureObj.wrapMode == TextureWrapMode.Mirror && root.Samplers[i].WrapS == WrapMode.MirroredRepeat;

				if (filterMatched && wrapMatched)
				{
					return new SamplerId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		protected static DrawMode GetDrawMode(MeshTopology topology)
		{
			switch (topology)
			{
				case MeshTopology.Points: return DrawMode.Points;
				case MeshTopology.Lines: return DrawMode.Lines;
				case MeshTopology.LineStrip: return DrawMode.LineStrip;
				case MeshTopology.Triangles: return DrawMode.Triangles;
			}

			throw new Exception("glTF does not support Unity mesh topology: " + topology);
		}
	}
}
