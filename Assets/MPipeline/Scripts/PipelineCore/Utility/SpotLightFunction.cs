using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
namespace MPipeline
{
    public unsafe struct RenderSpotShadowCommand
    {
        public Vector4[] frustumPlanes { get; private set; }
        public RenderTexture renderTarget;
        public SpotLightMatrix* shadowMatrices;
        public Material clusterShadowMaterial;
        public Camera currentCam;
        public void Init(Shader shadowShader)
        {
            frustumPlanes = new Vector4[6];
            clusterShadowMaterial = new Material(shadowShader);
        }
        public Vector4[] GetCullingPlane(float4* cullingPlanes)
        {
            UnsafeUtility.MemCpy(frustumPlanes.Ptr(), cullingPlanes, sizeof(float4) * 6);
            return frustumPlanes;
        }
        public void Dispose()
        {
            Object.DestroyImmediate(clusterShadowMaterial);
            frustumPlanes = null;
        }
    }
    public unsafe struct SpotLightMatrix
    {
        public int2 index;
        public void* mLightPtr;
        public Matrix4x4 projectionMatrix;
        public Matrix4x4 worldToCamera;
        public NativeList_Int customCulledResult;
        public float4 frustumPlane0;
        public float4 frustumPlane1;
        public float4 frustumPlane2;
        public float4 frustumPlane3;
        public float4 frustumPlane4;
        public float4 frustumPlane5;
    }
}