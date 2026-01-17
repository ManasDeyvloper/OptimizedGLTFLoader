using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// TRUE streaming GLTF loader - loads mesh buffers on-demand
/// Only loads what's visible in camera frustum
/// Requires: GLTF with external .bin files (not GLB)
/// </summary>
public class GLTFTrueStreamingLoader : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private string gltfFilePath = "path/to/your/model.gltf";
    [SerializeField] private Camera targetCamera;
    [SerializeField] private float updateInterval = 0.5f;
    [SerializeField] private float loadDistance = 100f;
    [SerializeField] private float unloadDistance = 150f;
    [SerializeField] private int maxConcurrentLoads = 3;

    [Header("Texture Settings")]
    [SerializeField] private bool generateMipmaps = false; // Disable for huge models
    [SerializeField] private int maxTextureSize = 2048; // Limit texture resolution
    [SerializeField] private bool compressTextures = false; // Use DXT compression (disable if issues)

    private GLTFRoot gltfRoot;
    private string gltfDirectory;
    private Dictionary<int, NodeData> nodeRegistry = new Dictionary<int, NodeData>();
    private Dictionary<string, byte[]> bufferCache = new Dictionary<string, byte[]>();
    private Dictionary<int, Material> materialCache = new Dictionary<int, Material>();
    private Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
    private Queue<NodeData> loadQueue = new Queue<NodeData>();
    private HashSet<NodeData> currentlyLoading = new HashSet<NodeData>();
    private GameObject rootObject;
    private float lastUpdateTime;
    private bool isInitialized = false;

    private class NodeData
    {
        public int nodeIndex;
        public string nodeName;
        public GameObject gameObject;
        public Transform transform;
        public int meshIndex;
        public Bounds bounds;
        public bool isLoaded;
        public bool isVisible;
        public MeshRenderer renderer;
        public MeshFilter filter;
        public List<string> requiredBuffers = new List<string>();
        public List<int> materialIndices = new List<int>();
    }

    private void Start()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        _ = Initialize();
    }

    private async Task Initialize()
    {
        Debug.Log("Initializing TRUE GLTF streaming...");

        // Get directory for loading external files
        gltfDirectory = Path.GetDirectoryName(gltfFilePath);

        // Load and parse GLTF JSON
        if (!await LoadGLTFStructure())
        {
            Debug.LogError("Failed to load GLTF structure");
            return;
        }

        // Create scene hierarchy without mesh data
        CreateSceneHierarchy();

        isInitialized = true;
        Debug.Log($"GLTF structure loaded. Found {nodeRegistry.Count} nodes to stream.");
    }

    private async Task<bool> LoadGLTFStructure()
    {
        try
        {
            string json = await File.ReadAllTextAsync(gltfFilePath);
            gltfRoot = JsonConvert.DeserializeObject<GLTFRoot>(json);
            return gltfRoot != null;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to parse GLTF: {e.Message}");
            return false;
        }
    }

    private void CreateSceneHierarchy()
    {
        rootObject = new GameObject("GLTF_Root");

        if (gltfRoot.scenes == null || gltfRoot.scenes.Count == 0)
            return;

        var scene = gltfRoot.scenes[gltfRoot.scene];

        foreach (var nodeIndex in scene.nodes)
        {
            ProcessNode(nodeIndex, Matrix4x4.identity, rootObject.transform);
        }
    }

    private void ProcessNode(int nodeIndex, Matrix4x4 parentMatrix, Transform parent)
    {
        if (nodeIndex >= gltfRoot.nodes.Count)
            return;

        var node = gltfRoot.nodes[nodeIndex];

        // Calculate transform
        Matrix4x4 localMatrix = GetNodeMatrix(node);
        Matrix4x4 worldMatrix = parentMatrix * localMatrix;

        // Create GameObject
        GameObject nodeObj = new GameObject(node.name ?? $"Node_{nodeIndex}");
        nodeObj.transform.SetParent(parent);
        ApplyTransform(nodeObj.transform, node);

        // If has mesh, register for streaming
        if (node.mesh >= 0 && node.mesh < gltfRoot.meshes.Count)
        {
            var nodeData = new NodeData
            {
                nodeIndex = nodeIndex,
                nodeName = node.name,
                gameObject = nodeObj,
                transform = nodeObj.transform,
                meshIndex = node.mesh,
                bounds = CalculateBounds(node.mesh, worldMatrix),
                isLoaded = false,
                isVisible = false
            };

            // Add mesh components (but no data yet)
            nodeData.filter = nodeObj.AddComponent<MeshFilter>();
            nodeData.renderer = nodeObj.AddComponent<MeshRenderer>();
            nodeData.renderer.enabled = false;

            // Determine which buffers this mesh needs
            DetermineRequiredBuffers(nodeData);

            nodeRegistry[nodeIndex] = nodeData;
        }

        // Process children
        if (node.children != null)
        {
            foreach (var childIndex in node.children)
            {
                ProcessNode(childIndex, worldMatrix, nodeObj.transform);
            }
        }
    }

    private void DetermineRequiredBuffers(NodeData nodeData)
    {
        var mesh = gltfRoot.meshes[nodeData.meshIndex];
        HashSet<int> bufferIndices = new HashSet<int>();

        foreach (var primitive in mesh.primitives)
        {
            // Store material indices
            if (primitive.material >= 0)
            {
                nodeData.materialIndices.Add(primitive.material);
            }

            // Get all accessors used by this primitive
            var accessorIndices = new List<int>();

            if (primitive.attributes != null)
            {
                if (primitive.attributes.POSITION >= 0) accessorIndices.Add(primitive.attributes.POSITION);
                if (primitive.attributes.NORMAL >= 0) accessorIndices.Add(primitive.attributes.NORMAL);
                if (primitive.attributes.TEXCOORD_0 >= 0) accessorIndices.Add(primitive.attributes.TEXCOORD_0);
                if (primitive.attributes.TANGENT >= 0) accessorIndices.Add(primitive.attributes.TANGENT);
            }

            if (primitive.indices >= 0)
                accessorIndices.Add(primitive.indices);

            // Find which buffers these accessors reference
            foreach (var accIndex in accessorIndices)
            {
                if (accIndex < gltfRoot.accessors.Count)
                {
                    var accessor = gltfRoot.accessors[accIndex];
                    if (accessor.bufferView >= 0 && accessor.bufferView < gltfRoot.bufferViews.Count)
                    {
                        var bufferView = gltfRoot.bufferViews[accessor.bufferView];
                        if (bufferView.buffer >= 0 && bufferView.buffer < gltfRoot.buffers.Count)
                        {
                            bufferIndices.Add(bufferView.buffer);
                        }
                    }
                }
            }
        }

        // Convert buffer indices to URIs
        foreach (var bufferIndex in bufferIndices)
        {
            var buffer = gltfRoot.buffers[bufferIndex];
            if (!string.IsNullOrEmpty(buffer.uri))
            {
                nodeData.requiredBuffers.Add(buffer.uri);
            }
        }
    }

    private Matrix4x4 GetNodeMatrix(GLTFNode node)
    {
        if (node.matrix != null && node.matrix.Length == 16)
        {
            return ArrayToMatrix(node.matrix);
        }

        Vector3 t = node.translation != null ? new Vector3(node.translation[0], node.translation[1], node.translation[2]) : Vector3.zero;
        Quaternion r = node.rotation != null ? new Quaternion(node.rotation[0], node.rotation[1], node.rotation[2], node.rotation[3]) : Quaternion.identity;
        Vector3 s = node.scale != null ? new Vector3(node.scale[0], node.scale[1], node.scale[2]) : Vector3.one;

        return Matrix4x4.TRS(t, r, s);
    }

    private void ApplyTransform(Transform transform, GLTFNode node)
    {
        if (node.translation != null)
            transform.localPosition = new Vector3(node.translation[0], node.translation[1], node.translation[2]);
        if (node.rotation != null)
            transform.localRotation = new Quaternion(node.rotation[0], node.rotation[1], node.rotation[2], node.rotation[3]);
        if (node.scale != null)
            transform.localScale = new Vector3(node.scale[0], node.scale[1], node.scale[2]);
    }

    private Bounds CalculateBounds(int meshIndex, Matrix4x4 worldMatrix)
    {
        var mesh = gltfRoot.meshes[meshIndex];
        Bounds bounds = new Bounds();
        bool initialized = false;

        foreach (var primitive in mesh.primitives)
        {
            if (primitive.attributes?.POSITION >= 0)
            {
                var accessor = gltfRoot.accessors[primitive.attributes.POSITION];
                if (accessor.min != null && accessor.max != null)
                {
                    Vector3 min = new Vector3(accessor.min[0], accessor.min[1], accessor.min[2]);
                    Vector3 max = new Vector3(accessor.max[0], accessor.max[1], accessor.max[2]);

                    min = worldMatrix.MultiplyPoint3x4(min);
                    max = worldMatrix.MultiplyPoint3x4(max);

                    if (!initialized)
                    {
                        bounds = new Bounds((min + max) / 2, max - min);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(min);
                        bounds.Encapsulate(max);
                    }
                }
            }
        }

        return bounds;
    }

    private Matrix4x4 ArrayToMatrix(float[] arr)
    {
        Matrix4x4 m = new Matrix4x4();
        m.m00 = arr[0]; m.m01 = arr[4]; m.m02 = arr[8]; m.m03 = arr[12];
        m.m10 = arr[1]; m.m11 = arr[5]; m.m12 = arr[9]; m.m13 = arr[13];
        m.m20 = arr[2]; m.m21 = arr[6]; m.m22 = arr[10]; m.m23 = arr[14];
        m.m30 = arr[3]; m.m31 = arr[7]; m.m32 = arr[11]; m.m33 = arr[15];
        return m;
    }

    private void Update()
    {
        if (!isInitialized || Time.time - lastUpdateTime < updateInterval)
            return;

        lastUpdateTime = Time.time;
        UpdateVisibility();
        ProcessLoadQueue();
    }

    private void UpdateVisibility()
    {
        var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(targetCamera);
        var cameraPos = targetCamera.transform.position;

        foreach (var nodeData in nodeRegistry.Values)
        {
            float distance = Vector3.Distance(cameraPos, nodeData.bounds.center);
            bool inFrustum = GeometryUtility.TestPlanesAABB(frustumPlanes, nodeData.bounds);
            bool shouldLoad = inFrustum && distance < loadDistance;
            /* bool shouldUnload = distance > unloadDistance; */
            Vector3 closestPoint = nodeData.bounds.ClosestPoint(cameraPos);
            bool cameraInsideBounds = (closestPoint - cameraPos).sqrMagnitude < 0.0001f;

            bool shouldUnload = distance > unloadDistance && !cameraInsideBounds;


            if (shouldLoad && !nodeData.isLoaded && !currentlyLoading.Contains(nodeData))
            {
                // Queue for loading
                if (!loadQueue.Contains(nodeData))
                {
                    loadQueue.Enqueue(nodeData);
                }
            }
            else if (shouldUnload && nodeData.isLoaded)
            {
                UnloadMesh(nodeData);
            }

            // Update visibility
            if (nodeData.renderer != null)
            {
                nodeData.renderer.enabled = nodeData.isLoaded && inFrustum && distance < loadDistance;
                nodeData.isVisible = nodeData.renderer.enabled;
            }
        }
    }

    private async void ProcessLoadQueue()
    {
        while (loadQueue.Count > 0 && currentlyLoading.Count < maxConcurrentLoads)
        {
            var nodeData = loadQueue.Dequeue();

            if (nodeData.isLoaded || currentlyLoading.Contains(nodeData))
                continue;

            currentlyLoading.Add(nodeData);
            await LoadMesh(nodeData);
            currentlyLoading.Remove(nodeData);
        }
    }

    private async Task LoadMesh(NodeData nodeData)
    {
        Debug.Log($"Loading mesh for {nodeData.nodeName}");

        try
        {
            // Load required buffers
            foreach (var bufferUri in nodeData.requiredBuffers)
            {
                if (!bufferCache.ContainsKey(bufferUri))
                {
                    string bufferPath = Path.Combine(gltfDirectory, bufferUri);
                    byte[] bufferData = await File.ReadAllBytesAsync(bufferPath);
                    bufferCache[bufferUri] = bufferData;
                    Debug.Log($"Loaded buffer: {bufferUri} ({bufferData.Length} bytes)");
                }
            }

            // Build Unity mesh from GLTF data
            Mesh mesh = await BuildMeshFromGLTF(nodeData.meshIndex);

            if (mesh != null)
            {
                nodeData.filter.sharedMesh = mesh;

                // Load materials
                var materials = new List<Material>();
                foreach (var matIndex in nodeData.materialIndices)
                {
                    Material material = await GetOrLoadMaterial(matIndex);
                    materials.Add(material);
                }

                // Apply materials
                if (materials.Count > 0)
                {
                    nodeData.renderer.sharedMaterials = materials.ToArray();
                }
                else
                {
                    // Fallback material
                    nodeData.renderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                }

                nodeData.isLoaded = true;
                nodeData.renderer.enabled = true;

                Debug.Log($"Mesh loaded: {nodeData.nodeName}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to load mesh {nodeData.nodeName}: {e.Message}");
        }
    }

    private async Task<Mesh> BuildMeshFromGLTF(int meshIndex)
    {
        var gltfMesh = gltfRoot.meshes[meshIndex];
        var primitive = gltfMesh.primitives[0]; // Simplified: just first primitive

        Mesh mesh = new Mesh();
        mesh.name = gltfMesh.name ?? $"Mesh_{meshIndex}";

        // Load positions
        if (primitive.attributes.POSITION >= 0)
        {
            Vector3[] positions = await ReadAccessorVector3(primitive.attributes.POSITION);
            mesh.vertices = positions;
        }

        // Load normals
        if (primitive.attributes.NORMAL >= 0)
        {
            Vector3[] normals = await ReadAccessorVector3(primitive.attributes.NORMAL);
            mesh.normals = normals;
        }

        // Load UVs - GLTF uses different UV coordinate system than Unity
        if (primitive.attributes.TEXCOORD_0 >= 0)
        {
            Vector2[] uvs = await ReadAccessorVector2(primitive.attributes.TEXCOORD_0);

            // Flip V coordinate (Y-axis) - GLTF has origin at top-left, Unity at bottom-left
            for (int i = 0; i < uvs.Length; i++)
            {
                uvs[i].y = 1.0f - uvs[i].y;
            }

            mesh.uv = uvs;
        }

        // Load indices
        if (primitive.indices >= 0)
        {
            int[] indices = await ReadAccessorIndices(primitive.indices);
            mesh.triangles = indices;
        }

        mesh.RecalculateBounds();
        if (primitive.attributes.NORMAL < 0)
            mesh.RecalculateNormals();

        return mesh;
    }

    private async Task<Vector3[]> ReadAccessorVector3(int accessorIndex)
    {
        var accessor = gltfRoot.accessors[accessorIndex];
        var bufferView = gltfRoot.bufferViews[accessor.bufferView];
        var buffer = gltfRoot.buffers[bufferView.buffer];

        byte[] bufferData = bufferCache[buffer.uri];

        Vector3[] data = new Vector3[accessor.count];
        int offset = (accessor.byteOffset ?? 0) + (bufferView.byteOffset ?? 0);

        for (int i = 0; i < accessor.count; i++)
        {
            int idx = offset + i * 12; // 3 floats * 4 bytes
            data[i] = new Vector3(
                System.BitConverter.ToSingle(bufferData, idx),
                System.BitConverter.ToSingle(bufferData, idx + 4),
                System.BitConverter.ToSingle(bufferData, idx + 8)
            );
        }

        return data;
    }

    private async Task<Vector2[]> ReadAccessorVector2(int accessorIndex)
    {
        var accessor = gltfRoot.accessors[accessorIndex];
        var bufferView = gltfRoot.bufferViews[accessor.bufferView];
        var buffer = gltfRoot.buffers[bufferView.buffer];

        byte[] bufferData = bufferCache[buffer.uri];

        Vector2[] data = new Vector2[accessor.count];
        int offset = (accessor.byteOffset ?? 0) + (bufferView.byteOffset ?? 0);

        for (int i = 0; i < accessor.count; i++)
        {
            int idx = offset + i * 8;
            data[i] = new Vector2(
                System.BitConverter.ToSingle(bufferData, idx),
                System.BitConverter.ToSingle(bufferData, idx + 4)
            );
        }

        return data;
    }

    private async Task<int[]> ReadAccessorIndices(int accessorIndex)
    {
        var accessor = gltfRoot.accessors[accessorIndex];
        var bufferView = gltfRoot.bufferViews[accessor.bufferView];
        var buffer = gltfRoot.buffers[bufferView.buffer];

        byte[] bufferData = bufferCache[buffer.uri];

        int[] indices = new int[accessor.count];
        int offset = (accessor.byteOffset ?? 0) + (bufferView.byteOffset ?? 0);

        // Handle different index types
        if (accessor.componentType == 5123) // UNSIGNED_SHORT
        {
            for (int i = 0; i < accessor.count; i++)
            {
                indices[i] = System.BitConverter.ToUInt16(bufferData, offset + i * 2);
            }
        }
        else if (accessor.componentType == 5125) // UNSIGNED_INT
        {
            for (int i = 0; i < accessor.count; i++)
            {
                indices[i] = (int)System.BitConverter.ToUInt32(bufferData, offset + i * 4);
            }
        }

        return indices;
    }

    private async Task<Material> GetOrLoadMaterial(int materialIndex)
    {
        // Check cache first
        if (materialCache.ContainsKey(materialIndex))
        {
            return materialCache[materialIndex];
        }

        // Load material
        if (gltfRoot.materials == null || materialIndex >= gltfRoot.materials.Count)
        {
            // Fallback material
            var fallback = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            materialCache[materialIndex] = fallback;
            return fallback;
        }

        var gltfMat = gltfRoot.materials[materialIndex];
        Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.name = gltfMat.name ?? $"Material_{materialIndex}";

        // Handle PBR Metallic Roughness
        if (gltfMat.pbrMetallicRoughness != null)
        {
            var pbr = gltfMat.pbrMetallicRoughness;

            // Base color
            if (pbr.baseColorFactor != null && pbr.baseColorFactor.Length >= 4)
            {
                mat.SetColor("_BaseColor", new Color(
                    pbr.baseColorFactor[0],
                    pbr.baseColorFactor[1],
                    pbr.baseColorFactor[2],
                    pbr.baseColorFactor[3]
                ));
            }
            else
            {
                mat.SetColor("_BaseColor", Color.white);
            }

            // Base color texture
            if (pbr.baseColorTexture != null && pbr.baseColorTexture.index >= 0)
            {
                Texture2D tex = await LoadTexture(pbr.baseColorTexture.index, false);
                if (tex != null)
                {
                    mat.SetTexture("_BaseMap", tex);

                    // Set texture tiling and offset if specified
                    if (pbr.baseColorTexture.texCoord == 0)
                    {
                        mat.SetTextureScale("_BaseMap", Vector2.one);
                        mat.SetTextureOffset("_BaseMap", Vector2.zero);
                    }
                }
            }

            // Metallic and smoothness - GLTF defaults are 1.0 and 1.0
            float metallic = pbr.metallicFactor ?? 1.0f;
            float roughness = pbr.roughnessFactor ?? 1.0f;

            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", 1f - roughness); // URP uses smoothness (inverse of roughness)

            // Metallic texture (URP combines metallic and smoothness in one texture)
            if (pbr.metallicRoughnessTexture != null && pbr.metallicRoughnessTexture.index >= 0)
            {
                Texture2D tex = await LoadTexture(pbr.metallicRoughnessTexture.index, false);
                if (tex != null)
                {
                    mat.SetTexture("_MetallicGlossMap", tex);
                    // URP expects smoothness in alpha channel, GLTF has roughness in green channel
                    // Set source to tell URP where to read from
                    mat.SetFloat("_SmoothnessTextureChannel", 1); // 1 = Albedo Alpha (but we'll use as-is)
                }
            }
        }
        else
        {
            // No PBR data, use defaults
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.5f);
        }

        // Normal map
        if (gltfMat.normalTexture != null && gltfMat.normalTexture.index >= 0)
        {
            Texture2D tex = await LoadTexture(gltfMat.normalTexture.index, true);
            if (tex != null)
            {
                mat.SetTexture("_BumpMap", tex);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 1.0f);
            }
        }

        // Occlusion map
        if (gltfMat.occlusionTexture != null && gltfMat.occlusionTexture.index >= 0)
        {
            Texture2D tex = await LoadTexture(gltfMat.occlusionTexture.index, false);
            if (tex != null)
            {
                mat.SetTexture("_OcclusionMap", tex);
                mat.SetFloat("_OcclusionStrength", 1.0f);
            }
        }

        // Emissive
        if (gltfMat.emissiveFactor != null && gltfMat.emissiveFactor.Length >= 3)
        {
            Color emissive = new Color(
                gltfMat.emissiveFactor[0],
                gltfMat.emissiveFactor[1],
                gltfMat.emissiveFactor[2]
            );
            mat.SetColor("_EmissionColor", emissive);
            if (emissive != Color.black)
            {
                mat.EnableKeyword("_EMISSION");
            }
        }

        // Emissive texture
        if (gltfMat.emissiveTexture != null && gltfMat.emissiveTexture.index >= 0)
        {
            Texture2D tex = await LoadTexture(gltfMat.emissiveTexture.index, false);
            if (tex != null)
            {
                mat.SetTexture("_EmissionMap", tex);
                mat.EnableKeyword("_EMISSION");
            }
        }

        // Alpha mode
        if (gltfMat.alphaMode == "BLEND")
        {
            // Transparent blend mode
            mat.SetFloat("_Surface", 1); // 1 = Transparent
            mat.SetFloat("_Blend", 0); // 0 = Alpha
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        else if (gltfMat.alphaMode == "MASK")
        {
            // Alpha clipping
            mat.SetFloat("_Surface", 0); // 0 = Opaque
            mat.SetFloat("_AlphaClip", 1); // Enable alpha clipping
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            if (gltfMat.alphaCutoff.HasValue)
            {
                mat.SetFloat("_Cutoff", gltfMat.alphaCutoff.Value);
            }
            else
            {
                mat.SetFloat("_Cutoff", 0.5f);
            }
        }
        else
        {
            // Opaque
            mat.SetFloat("_Surface", 0); // 0 = Opaque
            mat.SetFloat("_AlphaClip", 0);
            mat.SetOverrideTag("RenderType", "Opaque");
        }

        // Double sided
        if (gltfMat.doubleSided)
        {
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        }
        else
        {
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
        }

        materialCache[materialIndex] = mat;
        return mat;
    }

    private async Task<Texture2D> LoadTexture(int textureIndex, bool isNormalMap = false)
    {
        if (gltfRoot.textures == null || textureIndex >= gltfRoot.textures.Count)
            return null;

        var gltfTexture = gltfRoot.textures[textureIndex];

        if (gltfTexture.source < 0 || gltfRoot.images == null || gltfTexture.source >= gltfRoot.images.Count)
            return null;

        var image = gltfRoot.images[gltfTexture.source];

        if (string.IsNullOrEmpty(image.uri))
            return null;

        // Check cache
        string cacheKey = image.uri + (isNormalMap ? "_normal" : "");
        if (textureCache.ContainsKey(cacheKey))
        {
            return textureCache[cacheKey];
        }

        // Load texture file
        string texturePath = Path.Combine(gltfDirectory, image.uri);

        if (!File.Exists(texturePath))
        {
            Debug.LogWarning($"Texture not found: {texturePath}");
            return null;
        }

        byte[] textureData = await File.ReadAllBytesAsync(texturePath);

        // Load uncompressed first to get dimensions and manipulate if needed
        Texture2D tempTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, isNormalMap);

        if (!tempTex.LoadImage(textureData))
        {
            Destroy(tempTex);
            return null;
        }

        // Downscale if needed
        int targetWidth = tempTex.width;
        int targetHeight = tempTex.height;

        if (maxTextureSize > 0 && (targetWidth > maxTextureSize || targetHeight > maxTextureSize))
        {
            float scale = Mathf.Min((float)maxTextureSize / targetWidth, (float)maxTextureSize / targetHeight);
            targetWidth = Mathf.FloorToInt(targetWidth * scale);
            targetHeight = Mathf.FloorToInt(targetHeight * scale);
        }

        // Ensure dimensions are multiple of 4 for DXT compression
        if (compressTextures)
        {
            targetWidth = Mathf.Max(4, (targetWidth + 3) / 4 * 4);
            targetHeight = Mathf.Max(4, (targetHeight + 3) / 4 * 4);
        }

        // Resize if dimensions changed
        if (targetWidth != tempTex.width || targetHeight != tempTex.height)
        {
            TextureScale.Bilinear(tempTex, targetWidth, targetHeight);
        }

        // Now create final texture with proper format
        TextureFormat format = compressTextures
            ? (isNormalMap ? TextureFormat.DXT5 : TextureFormat.DXT1)
            : TextureFormat.RGBA32;

        Texture2D tex = new Texture2D(targetWidth, targetHeight, format, generateMipmaps, isNormalMap);

        // Copy pixels from temp texture
        if (!compressTextures)
        {
            tex.SetPixels(tempTex.GetPixels());
        }
        else
        {
            // For compressed textures, we need to set pixels then compress
            tex.SetPixels(tempTex.GetPixels());
        }

        Destroy(tempTex); // Clean up temporary texture

        {

            tex.name = Path.GetFileNameWithoutExtension(image.uri);

            // Apply sampler settings if available
            if (gltfTexture.sampler >= 0 && gltfRoot.samplers != null && gltfTexture.sampler < gltfRoot.samplers.Count)
            {
                var sampler = gltfRoot.samplers[gltfTexture.sampler];

                // Wrap mode
                tex.wrapModeU = ConvertWrapMode(sampler.wrapS);
                tex.wrapModeV = ConvertWrapMode(sampler.wrapT);

                // Filter mode
                if (sampler.magFilter == 9729 || sampler.minFilter == 9729) // LINEAR
                {
                    tex.filterMode = FilterMode.Bilinear;
                }
                else if (generateMipmaps && sampler.minFilter >= 9984) // MIPMAP modes
                {
                    tex.filterMode = FilterMode.Trilinear;
                }
                else
                {
                    tex.filterMode = FilterMode.Point;
                }
            }
            else
            {
                // Default settings
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = generateMipmaps ? FilterMode.Bilinear : FilterMode.Point;
            }

            // Compress and apply
            if (compressTextures)
            {
                tex.Compress(true); // High quality compression
            }

            tex.Apply(generateMipmaps, true); // Make read-only after applying

            textureCache[cacheKey] = tex;
            Debug.Log($"Loaded texture: {image.uri} ({tex.width}x{tex.height}, compressed: {compressTextures}, normal: {isNormalMap}, mipmaps: {generateMipmaps})");
            return tex;
        }

        return null;
    }

    private TextureWrapMode ConvertWrapMode(int gltfWrapMode)
    {
        switch (gltfWrapMode)
        {
            case 33071: return TextureWrapMode.Clamp;
            case 33648: return TextureWrapMode.Mirror;
            case 10497: return TextureWrapMode.Repeat;
            default: return TextureWrapMode.Repeat;
        }
    }

    private void UnloadMesh(NodeData nodeData)
    {
        if (!nodeData.isLoaded)
            return;

        Debug.Log($"Unloading mesh: {nodeData.nodeName}");

        if (nodeData.filter.sharedMesh != null)
        {
            Destroy(nodeData.filter.sharedMesh);
            nodeData.filter.sharedMesh = null;
        }

        // Don't destroy cached materials - they may be shared
        // Just remove the reference
        if (nodeData.renderer.sharedMaterials != null)
        {
            nodeData.renderer.sharedMaterials = null;
        }

        nodeData.isLoaded = false;
        nodeData.renderer.enabled = false;
    }

    private void OnDrawGizmos()
    {
        if (!isInitialized || nodeRegistry == null)
            return;

        foreach (var nodeData in nodeRegistry.Values)
        {
            if (nodeData.isLoaded && nodeData.isVisible)
                Gizmos.color = Color.green;
            else if (nodeData.isLoaded)
                Gizmos.color = Color.yellow;
            else
                Gizmos.color = Color.red;

            Gizmos.DrawWireCube(nodeData.bounds.center, nodeData.bounds.size);
        }
    }

    private void OnGUI()
    {
        if (!isInitialized) return;

        GUILayout.BeginArea(new Rect(10, 10, 300, 150));
        GUILayout.Label($"Total Nodes: {nodeRegistry.Count}");
        GUILayout.Label($"Loaded: {nodeRegistry.Values.Count(n => n.isLoaded)}");
        GUILayout.Label($"Visible: {nodeRegistry.Values.Count(n => n.isVisible)}");
        GUILayout.Label($"Loading: {currentlyLoading.Count}");
        GUILayout.Label($"Queued: {loadQueue.Count}");
        GUILayout.Label($"Buffers Cached: {bufferCache.Count}");
        GUILayout.EndArea();
    }

    private void OnDestroy()
    {
        bufferCache.Clear();

        // Clean up cached materials and textures
        foreach (var mat in materialCache.Values)
        {
            if (mat != null)
                Destroy(mat);
        }
        materialCache.Clear();

        foreach (var tex in textureCache.Values)
        {
            if (tex != null)
                Destroy(tex);
        }
        textureCache.Clear();

        if (rootObject != null)
            Destroy(rootObject);
    }
}

// Minimal GLTF data structures
[System.Serializable]
public class GLTFRoot
{
    public List<GLTFScene> scenes;
    public int scene;
    public List<GLTFNode> nodes;
    public List<GLTFMesh> meshes;
    public List<GLTFAccessor> accessors;
    public List<GLTFBufferView> bufferViews;
    public List<GLTFBuffer> buffers;
    public List<GLTFMaterial> materials;
    public List<GLTFTexture> textures;
    public List<GLTFImage> images;
    public List<GLTFSampler> samplers;
}

[System.Serializable]
public class GLTFScene
{
    public List<int> nodes;
}

[System.Serializable]
public class GLTFNode
{
    public string name;
    public int mesh = -1;
    public List<int> children;
    public float[] matrix;
    public float[] translation;
    public float[] rotation;
    public float[] scale;
}

[System.Serializable]
public class GLTFMesh
{
    public string name;
    public List<GLTFPrimitive> primitives;
}

[System.Serializable]
public class GLTFPrimitive
{
    public GLTFAttributes attributes;
    public int indices = -1;
    public int material = -1;
}

[System.Serializable]
public class GLTFAttributes
{
    public int POSITION = -1;
    public int NORMAL = -1;
    public int TEXCOORD_0 = -1;
    public int TANGENT = -1;
}

[System.Serializable]
public class GLTFAccessor
{
    public int bufferView = -1;
    public int? byteOffset;
    public int componentType;
    public int count;
    public string type;
    public float[] min;
    public float[] max;
}

[System.Serializable]
public class GLTFBufferView
{
    public int buffer;
    public int? byteOffset;
    public int byteLength;
}

[System.Serializable]
public class GLTFBuffer
{
    public string uri;
    public int byteLength;
}

[System.Serializable]
public class GLTFMaterial
{
    public string name;
    public GLTFPbrMetallicRoughness pbrMetallicRoughness;
    public GLTFTextureInfo normalTexture;
    public GLTFTextureInfo occlusionTexture;
    public GLTFTextureInfo emissiveTexture;
    public float[] emissiveFactor;
    public string alphaMode = "OPAQUE";
    public float? alphaCutoff;
    public bool doubleSided;
}

[System.Serializable]
public class GLTFPbrMetallicRoughness
{
    public float[] baseColorFactor;
    public GLTFTextureInfo baseColorTexture;
    public float? metallicFactor;
    public float? roughnessFactor;
    public GLTFTextureInfo metallicRoughnessTexture;
}

[System.Serializable]
public class GLTFTextureInfo
{
    public int index = -1;
    public int texCoord = 0;
}

[System.Serializable]
public class GLTFTexture
{
    public int sampler = -1;
    public int source = -1;
}

[System.Serializable]
public class GLTFImage
{
    public string uri;
    public string mimeType;
    public int bufferView = -1;
}

[System.Serializable]
public class GLTFSampler
{
    public int magFilter = 9729; // LINEAR
    public int minFilter = 9729; // LINEAR
    public int wrapS = 10497; // REPEAT
    public int wrapT = 10497; // REPEAT
}

// Utility class for texture scaling
public static class TextureScale
{
    public static void Bilinear(Texture2D tex, int newWidth, int newHeight)
    {
        var texColors = tex.GetPixels();
        var newColors = new Color[newWidth * newHeight];
        float ratioX = 1.0f / ((float)newWidth / (tex.width - 1));
        float ratioY = 1.0f / ((float)newHeight / (tex.height - 1));

        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                float xLerp = x * ratioX;
                float yLerp = y * ratioY;
                newColors[y * newWidth + x] = BilinearInterpolate(texColors, tex.width, tex.height, xLerp, yLerp);
            }
        }

        tex.Reinitialize(newWidth, newHeight);
        tex.SetPixels(newColors);
    }

    private static Color BilinearInterpolate(Color[] colors, int width, int height, float x, float y)
    {
        int x1 = Mathf.FloorToInt(x);
        int x2 = Mathf.Min(x1 + 1, width - 1);
        int y1 = Mathf.FloorToInt(y);
        int y2 = Mathf.Min(y1 + 1, height - 1);

        float xFrac = x - x1;
        float yFrac = y - y1;

        Color c11 = colors[y1 * width + x1];
        Color c21 = colors[y1 * width + x2];
        Color c12 = colors[y2 * width + x1];
        Color c22 = colors[y2 * width + x2];

        return Color.Lerp(Color.Lerp(c11, c21, xFrac), Color.Lerp(c12, c22, xFrac), yFrac);
    }
}