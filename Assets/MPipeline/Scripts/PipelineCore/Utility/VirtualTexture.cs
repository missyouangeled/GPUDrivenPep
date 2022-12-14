using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using static Unity.Mathematics.math;
using Unity.Mathematics;
using UnityEngine.Experimental.Rendering;

namespace MPipeline
{
    public enum VirtualTextureSize
    {
        x16 = 16,
        x32 = 32,
        x64 = 64,
        x128 = 128,
        x256 = 256,
        x512 = 512,
        x1024 = 1024,
        x2048 = 2048,
        x4096 = 4096,
        x8192 = 8192
    };
    public struct VirtualTextureFormat
    {
        public VirtualTextureSize perElementSize { get; private set; }
        public GraphicsFormat format { get; private set; }
        public int rtPropertyID { get; private set; }
        public int mipCount { get; private set; }
        public VirtualTextureFormat(VirtualTextureSize size, GraphicsFormat format, string rtName, int mipCount = 0)
        {
            perElementSize = size;
            this.mipCount = mipCount;
            this.format = format;
            rtPropertyID = Shader.PropertyToID(rtName);
        }
    }
    public unsafe struct VirtualTexture
    {
        private struct TexturePool
        {
            private NativeArray<bool> marks;
            private NativeList<int> arrayPool;
            public int LeftedElement
            {
                get
                {
                    return arrayPool.Length;
                }
            }
            public TexturePool(int capacity)
            {
                marks = new NativeArray<bool>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                arrayPool = new NativeList<int>(capacity, Allocator.Persistent);
                for (int i = 0; i < capacity; ++i)
                {
                    arrayPool.Add(i);
                }
            }
            public void Dispose()
            {
                marks.Dispose();
                arrayPool.Dispose();
            }
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void Return(int i)
            {
                if (i >= 0 && marks[i])
                {
                    marks[i] = false;
                    arrayPool.Add(i);
                }
            }
            public bool Get(out int t)
            {

                do
                {
                    if (arrayPool.Length <= 0)
                    {
                        t = -1;
                        return false;
                    }
                    t = arrayPool[arrayPool.Length - 1];
                    arrayPool.RemoveLast();
                } while (marks[t]);
                marks[t] = true;
                return true;
            }
        }
        private ComputeShader shader;
        private RenderTexture[] textures;
        private TexturePool pool;
        public int indexTexID { get; private set; }
        private NativeArray<VirtualTextureFormat> allFormats;
        private static int[] vtVariables = new int[4];
        private static int[] texSize = new int[2];
        private ComputeBuffer setIndexBuffer;
        private const int START_CHUNKSIZE = 8;
        public int2 indexSize { get; private set; }
        public RenderTexture indexTex { get; private set; }
        public struct VTChunkHandleEqual : IFunction<int2, int2, bool>
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public bool Run(ref int2 a, ref int2 b)
            {
                return a.x == b.x && a.y == b.y;
            }
        }
        private NativeDictionary<int2, int2, VTChunkHandleEqual> poolDict;
        public NativeDictionary<int2, int2, VTChunkHandleEqual> PoolDict => poolDict;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public RenderTexture GetTexture(int index)
        {
            return textures[index];
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public VirtualTextureFormat GetTextureFormat(int index)
        {
            return allFormats[index];
        }
        public int LeftedTextureElement
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get
            {
                return pool.LeftedElement;
            }
        }

        public void Update()
        {
            CommandBuffer beforeFrameBuffer = RenderPipeline.BeforeFrameBuffer;
            beforeFrameBuffer.SetGlobalTexture(indexTexID, indexTex);
            for (int i = 0; i < allFormats.Length; ++i)
            {
                beforeFrameBuffer.SetGlobalTexture(allFormats[i].rtPropertyID, textures[i]);
            }
        }
        public void Update(Material targetMaterial)
        {
            targetMaterial.SetTexture(indexTexID, indexTex);
            for (int i = 0; i < allFormats.Length; ++i)
            {
                targetMaterial.SetTexture(allFormats[i].rtPropertyID, textures[i]);
            }
        }
        /// <summary>
        /// Init Virtual Texture
        /// </summary>
        /// <param name="perTextureSize">Virtual texture's basic size</param>
        /// <param name="maximumSize">Virtual texture's array size</param>
        /// <param name="indexSize">Index Texture's size</param>
        /// <param name="formats">Each VT's format</param>
        public VirtualTexture(int maximumSize, int2 indexSize, VirtualTextureFormat* formats, int formatLen, string indexTexName)
        {
            if (maximumSize > 2048)
            {
                throw new System.Exception("Virtual Texture Maximum Size can not larger than 2048");
            }
            indexTexID = Shader.PropertyToID(indexTexName);
            this.indexSize = indexSize;
            setIndexBuffer = new ComputeBuffer(START_CHUNKSIZE * START_CHUNKSIZE, sizeof(uint));
            allFormats = new NativeArray<VirtualTextureFormat>(formatLen, Allocator.Persistent);
            poolDict = new NativeDictionary<int2, int2, VTChunkHandleEqual>(maximumSize, Allocator.Persistent, new VTChunkHandleEqual());
            UnsafeUtility.MemCpy(allFormats.GetUnsafePtr(), formats, sizeof(VirtualTextureFormat) * formatLen);
            shader = Resources.Load<ComputeShader>("VirtualTexture");
            pool = new TexturePool(maximumSize);
            indexTex = new RenderTexture(indexSize.x, indexSize.y, 0, GraphicsFormat.R16G16B16A16_UNorm, 0);
            indexTex.enableRandomWrite = true;
            indexTex.useMipMap = false;
            indexTex.filterMode = FilterMode.Point;
            indexTex.Create();
            textures = new RenderTexture[formatLen];
            for (int i = 0; i < formatLen; ++i)
            {
                ref VirtualTextureFormat format = ref formats[i];
                textures[i] = new RenderTexture((int)format.perElementSize, (int)format.perElementSize, 0, format.format, format.mipCount);
                textures[i].autoGenerateMips = false;
                textures[i].useMipMap = format.mipCount > 0;
                textures[i].enableRandomWrite = true;
                textures[i].volumeDepth = maximumSize;
                textures[i].dimension = TextureDimension.Tex2DArray;
                textures[i].antiAliasing = 1;
                textures[i].Create();
            }
        }
        public void Dispose()
        {
            Object.DestroyImmediate(indexTex);
            foreach (var i in textures)
            {
                Object.DestroyImmediate(i);
            }
            allFormats.Dispose();
            pool.Dispose();
            setIndexBuffer.Dispose();
            poolDict.Dispose();
        }

        public int GetChunkIndex(int2 startIndex)
        {
            startIndex %= indexSize;
            int2 result;
            if (poolDict.Get(startIndex, out result))
            {
                return result.y;
            }
            return -1;
        }

        public bool ContainsChunk(int2 startIndex, int size)
        {
            startIndex %= indexSize;
            int2 result;
            if (poolDict.Get(startIndex, out result))
            {
                if (result.x == size) return true;
            }
            return false;
        }

        private bool GetChunk(ref int2 startIndex, int size, out int element)
        {
            startIndex %= indexSize;
            int2 result;
            if (poolDict.Get(startIndex, out result))
            {
                if (result.x != size)
                {
                    result.x = size;
                    poolDict[startIndex] = result;
                }
                element = result.y;
                return true;
            }
            bool res = pool.Get(out element);
            result = int2(size, element);
            poolDict.Add(startIndex, result);
            return res;
        }

        private int UnloadChunk(ref int2 startIndex)
        {
            startIndex %= indexSize;
            int2 result;
            if (poolDict.Get(startIndex, out result))
            {
                poolDict.Remove(startIndex);
                pool.Return(result.y);
                return result.x;
            }
            return 0;
        }


        /// <summary>
        /// load a new texture into virtual texture
        /// </summary>
        /// <param name="startIndex">Start Index in the index texture</param>
        /// <param name="size">Pixel count in index texture</param>
        /// <returns>The target array index in TextureArray, return -1 if the pool is full</returns>
        public bool LoadNewTexture(int2 startIndex, int size, out int element)
        {
            bool res = GetChunk(ref startIndex, size, out element);
            vtVariables[0] = startIndex.x;
            vtVariables[1] = startIndex.y;
            vtVariables[2] = size;
            vtVariables[3] = element;
            shader.SetInts(ShaderIDs._VTVariables, vtVariables);
            texSize[0] = indexSize.x;
            texSize[1] = indexSize.y;
            shader.SetInts(ShaderIDs._IndexTextureSize, texSize);
            shader.SetTexture(0, ShaderIDs._IndexTexture, indexTex);
            int dispatchCount = Mathf.CeilToInt(size / 8f);
            shader.Dispatch(0, dispatchCount, dispatchCount, 1);
            return res;
        }

        public void LoadNewTextureChunks(int2 startIndex, int chunkSize, int chunkCount, NativeArray<int> texsContainner)
        {
            if (chunkCount * chunkCount > setIndexBuffer.count)
            {
                setIndexBuffer.Dispose();
                setIndexBuffer = new ComputeBuffer(chunkCount * chunkCount, sizeof(uint));
            }
            int fullSize = chunkCount * chunkSize;
            int* ptr = texsContainner.Ptr();
            for (int y = 0; y < chunkCount; ++y)
                for (int x = 0; x < chunkCount; ++x)
                {
                    int2 idx = startIndex + int2(x * chunkSize, y * chunkSize);
                    bool res = GetChunk(ref idx, chunkSize, out ptr[x + y * chunkCount]);
                }
            startIndex %= indexSize;
            vtVariables[0] = startIndex.x;
            vtVariables[1] = startIndex.y;
            vtVariables[2] = chunkSize;
            vtVariables[3] = chunkCount;
            shader.SetInts(ShaderIDs._VTVariables, vtVariables);
            texSize[0] = indexSize.x;
            texSize[1] = indexSize.y;
            shader.SetInts(ShaderIDs._IndexTextureSize, texSize);
            shader.SetTexture(1, ShaderIDs._IndexTexture, indexTex);
            shader.SetBuffer(1, ShaderIDs._ElementBuffer, setIndexBuffer);
            setIndexBuffer.SetData(texsContainner);
            int dispatchCount = Mathf.CeilToInt(fullSize / 8f);
            shader.Dispatch(1, dispatchCount, dispatchCount, 1);
        }
        /// <summary>
        /// Unload space
        /// </summary>
        /// <param name="startIndex">Start Index in IndexTexture </param>
        /// <param name="size">Target Size in IndexTexture</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void UnloadTexture(int2 startIndex)
        {
            UnloadChunk(ref startIndex);
        }

        public int CombineQuadTextureImmiedietely(int2 leftDownIndex, int2 rightDownIndex, int2 leftUpIndex, int2 rightUpIndex, int2 targetIndex, int targetSize)
        {
            int leftDown = GetChunkIndex(leftDownIndex);
            int leftUp = GetChunkIndex(leftUpIndex);
            int rightDown = GetChunkIndex(rightDownIndex);
            int rightUp = GetChunkIndex(rightUpIndex);
            UnloadChunk(ref leftDownIndex);
            UnloadChunk(ref rightDownIndex);
            UnloadChunk(ref leftUpIndex);
            UnloadChunk(ref rightUpIndex);
            int targetElement;
            if (!GetChunk(ref targetIndex, targetSize, out targetElement)) return -1;
            if (leftDown < 0 || leftUp < 0 || rightDown < 0 || rightUp < 0) return -1;
            foreach (var i in textures)
            {
                RenderTexture tempRT = RenderTexture.GetTemporary(i.width * 2, i.height * 2, 0, i.graphicsFormat, 1);
                tempRT.filterMode = FilterMode.Bilinear;
                Graphics.CopyTexture(i, leftDown, 0, 0, 0, i.width, i.height, tempRT, 0, 0, 0, 0);
                Graphics.CopyTexture(i, rightDown, 0, 0, 0, i.width, i.height, tempRT, 0, 0, i.width, 0);
                Graphics.CopyTexture(i, leftUp, 0, 0, 0, i.width, i.height, tempRT, 0, 0, 0, i.height);
                Graphics.CopyTexture(i, rightUp, 0, 0, 0, i.width, i.height, tempRT, 0, 0, i.width, i.height);
                shader.SetInt(ShaderIDs._TargetElement, targetElement);
                shader.SetInt(ShaderIDs._Count, i.width);
                shader.SetTexture(4, ShaderIDs._VirtualTexture, i);
                shader.SetTexture(4, ShaderIDs._MainTex, tempRT);
                int disp = i.width / 8;
                shader.Dispatch(4, disp, disp, 1);
                RenderTexture.ReleaseTemporary(tempRT);
            }
            vtVariables[0] = targetIndex.x;
            vtVariables[1] = targetIndex.y;
            vtVariables[2] = targetSize;
            vtVariables[3] = targetElement;
            shader.SetInts(ShaderIDs._VTVariables, vtVariables);
            texSize[0] = indexSize.x;
            texSize[1] = indexSize.y;
            shader.SetInts(ShaderIDs._IndexTextureSize, texSize);
            shader.SetTexture(0, ShaderIDs._IndexTexture, indexTex);
            int dispatchCount = Mathf.CeilToInt(targetSize / 8f);
            shader.Dispatch(0, dispatchCount, dispatchCount, 1);
            return targetElement;
        }

        public int CombineQuadTextures(int2 leftDownIndex, int2 rightDownIndex, int2 leftUpIndex, int2 rightUpIndex, int2 targetIndex, int targetSize, CommandBuffer buffer)
        {
            int leftDown = GetChunkIndex(leftDownIndex);
            int leftUp = GetChunkIndex(leftUpIndex);
            int rightDown = GetChunkIndex(rightDownIndex);
            int rightUp = GetChunkIndex(rightUpIndex);
            UnloadChunk(ref leftDownIndex);
            UnloadChunk(ref rightDownIndex);
            UnloadChunk(ref leftUpIndex);
            UnloadChunk(ref rightUpIndex);
            int targetElement;
            if (!GetChunk(ref targetIndex, targetSize, out targetElement)) return -1;
            if (leftDown < 0 || leftUp < 0 || rightDown < 0 || rightUp < 0) return -1;
            foreach (var i in textures)
            {
                buffer.GetTemporaryRT(ShaderIDs._TempPropBuffer, i.width * 2, i.height * 2, 0, FilterMode.Bilinear, i.graphicsFormat, 1, false);
                buffer.CopyTexture(i, leftDown, 0, 0, 0, i.width, i.height, ShaderIDs._TempPropBuffer, 0, 0, 0, 0);
                buffer.CopyTexture(i, rightDown, 0, 0, 0, i.width, i.height, ShaderIDs._TempPropBuffer, 0, 0, i.width, 0);
                buffer.CopyTexture(i, leftUp, 0, 0, 0, i.width, i.height, ShaderIDs._TempPropBuffer, 0, 0, 0, i.height);
                buffer.CopyTexture(i, rightUp, 0, 0, 0, i.width, i.height, ShaderIDs._TempPropBuffer, 0, 0, i.width, i.height);
                buffer.SetComputeIntParam(shader, ShaderIDs._TargetElement, targetElement);
                buffer.SetComputeIntParam(shader, ShaderIDs._Count, i.width);
                buffer.SetComputeTextureParam(shader, 4, ShaderIDs._VirtualTexture, i);
                buffer.SetComputeTextureParam(shader, 4, ShaderIDs._MainTex, ShaderIDs._TempPropBuffer);
                int disp = i.width / 8;
                buffer.DispatchCompute(shader, 4, disp, disp, 1);
                buffer.ReleaseTemporaryRT(ShaderIDs._TempPropBuffer);
            }
            vtVariables[0] = targetIndex.x;
            vtVariables[1] = targetIndex.y;
            vtVariables[2] = targetSize;
            vtVariables[3] = targetElement;
            shader.SetInts(ShaderIDs._VTVariables, vtVariables);
            texSize[0] = indexSize.x;
            texSize[1] = indexSize.y;
            shader.SetInts(ShaderIDs._IndexTextureSize, texSize);
            shader.SetTexture(0, ShaderIDs._IndexTexture, indexTex);
            int dispatchCount = Mathf.CeilToInt(targetSize / 8f);
            shader.Dispatch(0, dispatchCount, dispatchCount, 1);
            return targetElement;
        }
    }
}