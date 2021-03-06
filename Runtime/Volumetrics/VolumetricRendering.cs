﻿using System.Collections;
using System.Collections.Generic;
//using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

class VolumeRenderingUtils //Importing some functions from HDRP to have simular terms   
{
    public static float MeanFreePathFromExtinction(float extinction)
    {
        return 1.0f / extinction;
    }

    public static float ExtinctionFromMeanFreePath(float meanFreePath)
    {
        return 1.0f / meanFreePath;
    }

    public static Vector3 AbsorptionFromExtinctionAndScattering(float extinction, Vector3 scattering)
    {
        return new Vector3(extinction, extinction, extinction) - scattering;
    }

    public static Vector3 ScatteringFromExtinctionAndAlbedo(float extinction, Vector3 albedo)
    {
        return extinction * albedo;
    }

    public static Vector3 AlbedoFromMeanFreePathAndScattering(float meanFreePath, Vector3 scattering)
    {
        return meanFreePath * scattering;
    }
}

//TODO: Add semi dynamic lighting which is generated in the clipmap and not previously baked out. Will need smarter clipmap gen to avoid hitching.
//Add cascading clipmaps to have higher detail up close and include father clipping without exploding memory.
//Convert this to a render feature. This should remove the need for the platform switcher too because that would be handled by the quality settings pipeline asset instead


//[RequireComponent(typeof( Camera ) )]
[ExecuteInEditMode]
public class VolumetricRendering : MonoBehaviour
{

    #region variables
    public float tempOffset = 0;
    Texture3D BlackTex; //Temp texture for 

    public Camera cam; //Main camera to base settings on
    public VolumetricData volumetricData;
    [Range(0, 1)]
    public float reprojectionAmount = 0.5f;
    //   [Tooltip("Does a final blur pass on the rendered fog")]
    //    public bool FroxelBlur = false;

    [HideInInspector]
    public enum BlurType {None, Gaussian};
    public BlurType FroxelBlur = BlurType.None;


    //public Texture skytex;
    //[Header("Volumetric camera settings")]
    //[Tooltip("Near Clip plane")]
    //public float near = 1;
    //[Tooltip("Far Clip plane")]
    //public float far = 40;
    //[Tooltip("Resolution")]
    //public int FroxelWidthResolution = 128;
    //[Tooltip("Resolution")]
    //public int FroxelHeightResolution = 128;
    //[Tooltip("Resolution")]
    //public int FroxelDepthResolution = 64;
    ////[Tooltip("Controls the bias of the froxel dispution. A value of 1 is linear. ")]
    ////public float FroxelDispution;

    //[Header("Prebaked clipmap settings")]
    //[Tooltip("Textile resolution per unit")]
    //public int ClipMapResolution = 128;
    //[Tooltip("Size of clipmap in units")]
    //public float ClipmapScale = 80;
    //[Tooltip("Distance (m) from previous sampling point to trigger resampling clipmap")]
    //public float ClipmapResampleThreshold = 1;


    Vector3 ClipmapTransform; //Have this follow the camera and resample when the camera moves enough 
    Vector3 ClipmapCurrentPos; //chached location of previous sample point

    //public Matrix4x4 randomatrix;

    //Required shaders
    [SerializeField, HideInInspector] ComputeShader FroxelFogCompute;
    [SerializeField, HideInInspector] ComputeShader FroxelIntegrationCompute;
    [SerializeField, HideInInspector] ComputeShader ClipmapCompute;
    [SerializeField, HideInInspector] ComputeShader BlurCompute;

    //Texture buffers
    RenderTexture ClipmapBufferA;  //Sampling and combining baked maps asynchronously
    RenderTexture ClipmapBufferB;  //Sampling and combining baked maps asynchronously
    RenderTexture ClipmapBufferC;  //Sampling and combining baked maps asynchronously
    RenderTexture ClipmapBufferD;  //Sampling and combining baked maps asynchronously //TODO: get rid of this extra buffer and bool
    bool FlipClipBuffer = true;
    bool FlipClipBufferFar = true;

    RenderTexture FroxelBufferA;   //Single froxel projection use for scattering and history reprojection
    RenderTexture FroxelBufferB;   //for history reprojection

    RenderTexture IntegrationBuffer;    //Integration and stereo reprojection
                                        //  RenderTexture IntegrationBufferB;    //Integration and stereo reprojection
    RenderTexture BlurBuffer;    //blur
    RenderTexture BlurBufferB;    //blur


    // This is a sequence of 7 equidistant numbers from 1/14 to 13/14.
    // Each of them is the centroid of the interval of length 2/14.
    // They've been rearranged in a sequence of pairs {small, large}, s.t. (small + large) = 1.
    // That way, the running average position is close to 0.5.
    // | 6 | 2 | 4 | 1 | 5 | 3 | 7 |
    // |   |   |   | o |   |   |   |
    // |   | o |   | x |   |   |   |
    // |   | x |   | x |   | o |   |
    // |   | x | o | x |   | x |   |
    // |   | x | x | x | o | x |   |
    // | o | x | x | x | x | x |   |
    // | x | x | x | x | x | x | o |
    // | x | x | x | x | x | x | x |
    float[] m_zSeq = { 7.0f / 14.0f, 3.0f / 14.0f, 11.0f / 14.0f, 5.0f / 14.0f, 9.0f / 14.0f, 1.0f / 14.0f, 13.0f / 14.0f };


    // Ref: https://en.wikipedia.org/wiki/Close-packing_of_equal_spheres
    // The returned {x, y} coordinates (and all spheres) are all within the (-0.5, 0.5)^2 range.
    // The pattern has been rotated by 15 degrees to maximize the resolution along X and Y:
    // https://www.desmos.com/calculator/kcpfvltz7c
    static void GetHexagonalClosePackedSpheres7(Vector2[] coords)
    {

        float r = 0.17054068870105443882f;
        float d = 2 * r;
        float s = r * Mathf.Sqrt(3);

        // Try to keep the weighted average as close to the center (0.5) as possible.
        //  (7)(5)    ( )( )    ( )( )    ( )( )    ( )( )    ( )(o)    ( )(x)    (o)(x)    (x)(x)
        // (2)(1)(3) ( )(o)( ) (o)(x)( ) (x)(x)(o) (x)(x)(x) (x)(x)(x) (x)(x)(x) (x)(x)(x) (x)(x)(x)
        //  (4)(6)    ( )( )    ( )( )    ( )( )    (o)( )    (x)( )    (x)(o)    (x)(x)    (x)(x)
        coords[0] = new Vector2(0, 0);
        coords[1] = new Vector2(-d, 0);
        coords[2] = new Vector2(d, 0);
        coords[3] = new Vector2(-r, -s);
        coords[4] = new Vector2(r, s);
        coords[5] = new Vector2(r, -s);
        coords[6] = new Vector2(-r, s);

        // Rotate the sampling pattern by 15 degrees.
        const float cos15 = 0.96592582628906828675f;
        const float sin15 = 0.25881904510252076235f;

        for (int i = 0; i < 7; i++)
        {
            Vector2 coord = coords[i];

            coords[i].x = coord.x * cos15 - coord.y * sin15;
            coords[i].y = coord.x * sin15 + coord.y * cos15;
        }
    }

    Vector2[] m_xySeq = new Vector2[7];




    /// Dynamic Light Projection///      
    [SerializeField, HideInInspector] List<Light> Lights; // TODO: Make this a smart dynamic list not living here
    public struct LightObject
    {
        public Matrix4x4 LightProjectionMatrix;
        public Vector3 LightPosition;
        public Vector4 LightColor;
        public int LightCookie; //TODO: Add general light cookie system to render engine
    }

    //Figure out how much data is in the struct above
    int LightObjectStride = sizeof(float) * 4 * 4 + sizeof(float) * 3 + sizeof(float) * 4 + sizeof(int);

    Texture2DArray LightProjectionTextures; // TODO: Make this a smart dynamic list pulling from light cookies

    private static List<LightObject> LightObjects;
    ComputeBuffer LightBuffer;

    /// END Dynamic Light Projection/// 
    /// 

    // public Texture2D BlueNoise; //Temp ref

    //AABB 

    //Stored compute shader IDs and numbers

    protected int ScatteringKernel = 0;
    protected int IntegrateKernel = 0;
    protected int BlurKernelX = 0;
    protected int BlurKernelY = 0;

    Matrix4x4 matScaleBias;
    Vector3 ThreadsToDispatch;

    //Stored shader variable name IDs

    //Froxel Ids
    int CameraProjectionMatrixID = Shader.PropertyToID("CameraProjectionMatrix");
    int TransposedCameraProjectionMatrixID = Shader.PropertyToID("TransposedCameraProjectionMatrix");
    int inverseCameraProjectionMatrixID = Shader.PropertyToID("inverseCameraProjectionMatrix");
    int PreviousFrameMatrixID = Shader.PropertyToID("PreviousFrameMatrix");
    int Camera2WorldID = Shader.PropertyToID("Camera2World");
    int CameraPositionID = Shader.PropertyToID("CameraPosition");

    //Clipmap IDs
    int CameraMotionVectorID = Shader.PropertyToID("CameraMotionVector");
    int ClipmapTextureID = Shader.PropertyToID("_ClipmapTexture");
    int ClipmapTextureID2 = Shader.PropertyToID("_VolumetricClipmapTexture"); //TODO: Make these two the same name
    int ClipmapScaleID = Shader.PropertyToID("_ClipmapScale");
    int ClipmapTransformID = Shader.PropertyToID("_ClipmapPosition");

    int LightObjectsID = Shader.PropertyToID("LightObjects");

    //Temp Jitter stuff
    int tempjitter = 0; //TEMP jitter switcher thing 
    [Header("Extra variables"), Range(0, 1)]
    float[] jitters = new float[2] { 0.0f, 0.5f };


    //Previous view matrix data

    Matrix4x4 PreviousFrameMatrix = Matrix4x4.identity;
    Vector3 PreviousCameraPosition;
    Vector3 previousPos;
    Quaternion previousQuat;

    //General fog settings
    // [HideInInspector]
    [Header("Base values that are overridden by Volumes")]
    public Color albedo = Color.white;
    //    public Color extinctionTint = Color.white;
    public float meanFreePath = 15.0f;
    public float StaticLightMultiplier = 1.0f;

    #endregion

    private void Awake()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) return;
#endif
        Shader.EnableKeyword("_VOLUMETRICS_ENABLED"); //Enable volumetrics. Double check to see if works in build
        //  cam = GetComponent<Camera>();

    }
    void Start() {
#if UNITY_EDITOR
        if (!Application.isPlaying) return;
#endif
        Intialize();
    }
    void CheckCookieList()
    {
        if (LightProjectionTextures != null) return;
        LightProjectionTextures = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
        Debug.Log("Made blank cookie sheet");
    }

    //void dedbugRTC()
    //{
    //    RenderTexture.active = (RenderTexture)skytex;
    //    GL.Clear(true, true, Color.yellow);
    //    RenderTexture.active = null;

    //}
    //void SetSkyTexture(Texture cubemap)
    //{
    //  //  cam.RenderToCubemap((Cubemap)cubemap);
    // //   dedbugRTC();
    //    Shader.SetGlobalTexture("_SkyTexture", cubemap);
    //}

    bool VerifyVolumetricRegisters()
    {
        //Add realtime light check here too
        if (VolumetricRegisters.volumetricAreas.Count > 0) //brute force check
                                                           //  if (VolumetricRegisters.volumetricAreas.Count > 0)
        {
            Debug.Log(VolumetricRegisters.volumetricAreas.Count + " Volumes ready to render");
            return true;
        }
        Debug.Log("No Volumetric volumes in " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ". Disabling froxel rendering.");
        this.enabled = false;
        return false;
    }

    void CheckOverrideVolumes() //TODO: Is there a better way to do this?
    {
        var stack = VolumeManager.instance.stack;

        var Volumetrics = stack.GetComponent<Volumetrics>();
        if (Volumetrics != null)
            Volumetrics.PushFogShaderParameters();
    }

    void IntializeBlur(RenderTextureDescriptor rtdiscrpt)
    {
        BlurBuffer = new RenderTexture(rtdiscrpt);
        BlurBuffer.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
        BlurBuffer.enableRandomWrite = true;
        BlurBuffer.Create();

        BlurBufferB = new RenderTexture(rtdiscrpt);
        BlurBufferB.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
        BlurBufferB.enableRandomWrite = true;
        BlurBufferB.Create();

        BlurKernelX = BlurCompute.FindKernel("VolBlurX");
        BlurKernelY = BlurCompute.FindKernel("VolBlurY");
        BlurCompute.SetTexture(BlurKernelX, "InTex", IntegrationBuffer);
        BlurCompute.SetTexture(BlurKernelX, "Result", BlurBuffer);
        BlurCompute.SetTexture(BlurKernelY, "InTex", BlurBuffer);
        BlurCompute.SetTexture(BlurKernelY, "Result", BlurBufferB);
    }

    void Intialize()
    {
        CheckOverrideVolumes();
     //   if (VerifyVolumetricRegisters() == false) return; //Check registers to see if there's anything to render. If not, then disable system. TODO: Remove this 
        CheckCookieList();

     //   SetSkyTexture( skytex);

        //Making prescaled matrix 
        matScaleBias = Matrix4x4.identity;
        matScaleBias.m00 = -0.5f;
        matScaleBias.m11 = -0.5f;
        matScaleBias.m22 = 0.5f;
        matScaleBias.m03 = 0.5f;
        matScaleBias.m13 = 0.5f;
        matScaleBias.m23 = 0.5f;

        //Create 3D Render Texture 1
        RenderTextureDescriptor rtdiscrpt = new RenderTextureDescriptor();
        rtdiscrpt.enableRandomWrite = true;
        rtdiscrpt.dimension = TextureDimension.Tex3D;
        rtdiscrpt.width = volumetricData.FroxelWidthResolution;
        rtdiscrpt.height = volumetricData.FroxelHeightResolution;
        rtdiscrpt.volumeDepth = volumetricData.FroxelDepthResolution;
        rtdiscrpt.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
        rtdiscrpt.msaaSamples = 1;

        FroxelBufferA = new RenderTexture(rtdiscrpt);
        FroxelBufferA.Create();

        //Ugh... extra android buffer mess. Can I use a custom RT double buffer instead?
        FroxelBufferB = new RenderTexture(rtdiscrpt);
        FroxelBufferB.Create();



        rtdiscrpt.width = volumetricData.FroxelWidthResolution * 2; // Make double wide texture for stereo use. Make smarter for non VR use case?
        IntegrationBuffer = new RenderTexture(rtdiscrpt);
      //  IntegrationBuffer.format = RenderTextureFormat.ARGB32;
        IntegrationBuffer.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
        IntegrationBuffer.enableRandomWrite = true;
        IntegrationBuffer.Create();

        //IntegrationBufferB = new RenderTexture(rtdiscrpt);
        //IntegrationBufferB.format = RenderTextureFormat.ARGB32;
        //IntegrationBufferB.enableRandomWrite = true;
        //IntegrationBufferB.Create();

        if (FroxelBlur == BlurType.Gaussian) IntializeBlur(rtdiscrpt);

        LightObjects = new List<LightObject>();

        ScatteringKernel = FroxelFogCompute.FindKernel("Scatter");
        FroxelFogCompute.SetTexture(ScatteringKernel, "Result", FroxelBufferA);

        ScatteringKernel = FroxelFogCompute.FindKernel("Scatter");


        //First Compute pass setup

        SetupClipmap();

        FroxelFogCompute.SetFloat("ClipmapScale", volumetricData.ClipmapScale);
        FroxelFogCompute.SetFloat("_VBufferUnitDepthTexelSpacing", ComputZPlaneTexelSpacing(1, cam.fieldOfView, volumetricData.FroxelHeightResolution) );
        UpdateClipmap(Clipmap.Near);
        UpdateClipmap(Clipmap.Far);
        FroxelFogCompute.SetTexture(ScatteringKernel, ClipmapTextureID, ClipmapBufferA);
        FroxelFogCompute.SetTexture(ScatteringKernel, "LightProjectionTextureArray", LightProjectionTextures); // temp light cookie array. TODO: Make dynamic. Add to lighting engine too.
                                                                                                              //     FroxelFogCompute.SetTexture(FogFroxelKernel, "BlueNoise", BlueNoise); // temp light cookie array. TODO: Make dynamic. Add to lighting engine too.

        ///Second compute pass setup

        IntegrateKernel = FroxelIntegrationCompute.FindKernel("StepAdd");
        FroxelIntegrationCompute.SetTexture(IntegrateKernel, "Result", IntegrationBuffer);
        FroxelIntegrationCompute.SetTexture(IntegrateKernel, "InLightingTexture", FroxelBufferA);

        //Make view projection matricies

        Matrix4x4 CenterProjectionMatrix = matScaleBias * Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, volumetricData.near, volumetricData.far);
        Matrix4x4 LeftProjectionMatrix = matScaleBias * Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, volumetricData.near, volumetricData.far) * Matrix4x4.Translate(new Vector3(cam.stereoSeparation * 0.5f, 0, 0)); //temp ipd scaler. Combine factors when confirmed
        Matrix4x4 RightProjectionMatrix = matScaleBias * Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, volumetricData.near, volumetricData.far) * Matrix4x4.Translate(new Vector3(-cam.stereoSeparation * 0.5f, 0, 0));

        //Debug.Log(cam.stereoSeparation);

        FroxelIntegrationCompute.SetMatrix("LeftEyeMatrix", LeftProjectionMatrix * CenterProjectionMatrix.inverse);
        FroxelIntegrationCompute.SetMatrix("RightEyeMatrix", RightProjectionMatrix * CenterProjectionMatrix.inverse);


        //Global Variable setup

     if ((FroxelBlur == BlurType.Gaussian)) Shader.SetGlobalTexture("_VolumetricResult", BlurBufferB);
     else   Shader.SetGlobalTexture("_VolumetricResult", IntegrationBuffer);

        ThreadsToDispatch = new Vector3(
             Mathf.CeilToInt(volumetricData.FroxelWidthResolution / 4.0f),
             Mathf.CeilToInt(volumetricData.FroxelHeightResolution / 4.0f),
             Mathf.CeilToInt(volumetricData.FroxelDepthResolution / 4.0f)
            );

    //    ComputZPlaneTexelSpacing(1.0f, vFoV, parameters.resolution.y);


        Shader.SetGlobalVector("_VolumePlaneSettings", new Vector4(volumetricData.near, volumetricData.far, volumetricData.far - volumetricData.near, volumetricData.near * volumetricData.far));

        float zBfP1 = 1.0f - volumetricData.far / volumetricData.near;
        float zBfP2 = volumetricData.far / volumetricData.near;
        Shader.SetGlobalVector("_ZBufferParams", new Vector4(zBfP1, zBfP2, zBfP1 / volumetricData.far, zBfP2 / volumetricData.far));

        Debug.Log("Dispatching " + ThreadsToDispatch);

        SetVariables();
    }

    void UpdateLights()
    {
        LightObjects.Clear(); //clear and rebuild for now. TODO: Make a smarter constructor
        if (LightBuffer != null) LightBuffer.Release();

        for (int i = 0; i < Lights.Count; i++)
        {
            LightObject lightObject = new LightObject();
            lightObject.LightPosition = Lights[i].transform.position;
            lightObject.LightColor = new Color(
                Lights[i].color.r * Lights[i].intensity,
                Lights[i].color.g * Lights[i].intensity,
                Lights[i].color.b * Lights[i].intensity,
                Lights[i].color.a);
            lightObject.LightProjectionMatrix = matScaleBias
                * Matrix4x4.Perspective(Lights[i].spotAngle, 1, 0.1f, Lights[i].range)
                * Matrix4x4.Rotate(Lights[i].transform.rotation).inverse;

            LightObjects.Add(lightObject);
        }
        LightBuffer = new ComputeBuffer(LightObjects.Count, LightObjectStride);
        LightBuffer.SetData(LightObjects);
        FroxelFogCompute.SetBuffer(ScatteringKernel, LightObjectsID, LightBuffer); // TODO: move to an int
    }
    #region Clipmap funtions
    void SetupClipmap()
    {

        RenderTextureDescriptor ClipRTdiscrpt = new RenderTextureDescriptor();
        ClipRTdiscrpt.enableRandomWrite = true;
        ClipRTdiscrpt.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        ClipRTdiscrpt.width = volumetricData.ClipMapResolution;
        ClipRTdiscrpt.height = volumetricData.ClipMapResolution;    
        ClipRTdiscrpt.volumeDepth = volumetricData.ClipMapResolution;
        ClipRTdiscrpt.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat;
        ClipRTdiscrpt.msaaSamples = 1;

        ClipmapBufferA = new RenderTexture(ClipRTdiscrpt);
        ClipmapBufferA.Create();
        ClipmapBufferB = new RenderTexture(ClipRTdiscrpt);
        ClipmapBufferB.Create();        
        ClipmapBufferC = new RenderTexture(ClipRTdiscrpt);
        ClipmapBufferC.Create();        
        ClipmapBufferD = new RenderTexture(ClipRTdiscrpt);
        ClipmapBufferD.Create();
        //TODO: Loop through and remove one of the buffers

        Shader.SetGlobalTexture("_VolumetricClipmapTexture", ClipmapBufferA); //Set clipmap for
        Shader.SetGlobalFloat("_ClipmapScale", volumetricData.ClipmapScale);
        Shader.SetGlobalFloat("_ClipmapScale2", volumetricData.ClipmapScale2);
    }
    bool ClipFar = false;
    void CheckClipmap() //Check distance from previous sample and recalulate if over threshold. TODO: make it resample chunks
    {

        if (Vector3.Distance(ClipmapCurrentPos, cam.transform.position) > volumetricData.ClipmapResampleThreshold)
        {
            //TODO: seperate the frames where this is rendered
            UpdateClipmap(Clipmap.Near);
            UpdateClipmap(Clipmap.Far);
            //if (ClipFar == false) UpdateClipmap(Clipmap.Near);
            //else {
            //    UpdateClipmap(Clipmap.Far);
            //    ClipFar = false;
            //    };
        }
    }

    enum Clipmap { Near,Far};


    void UpdateClipmap(Clipmap clipmap)
    {
        //TODO: chache ids 
        int ClipmapKernal = ClipmapCompute.FindKernel("ClipMapGen");
        int ClearClipmapKernal = ClipmapCompute.FindKernel("ClipMapClear");
        ClipmapTransform = cam.transform.position;

        float farscale = volumetricData.ClipmapScale2;

        RenderTexture BufferA;
        RenderTexture BufferB;
        //TODO: bake out variables at start to avoid extra math per clip gen
        
        if (clipmap == Clipmap.Near)
        {
            BufferA = ClipmapBufferB;
            BufferB = ClipmapBufferA;
            ClipmapCompute.SetFloat("ClipmapScale", volumetricData.ClipmapScale);
            ClipmapCompute.SetVector("ClipmapWorldPosition", ClipmapTransform - (0.5f * volumetricData.ClipmapScale * Vector3.one));

        }
        else
        {
            BufferA = ClipmapBufferC;
            BufferB = ClipmapBufferD;

            ClipmapCompute.SetFloat("ClipmapScale", volumetricData.ClipmapScale2);
            ClipmapCompute.SetVector("ClipmapWorldPosition", ClipmapTransform - (0.5f * volumetricData.ClipmapScale2 * Vector3.one));

        }

        //Clipmap variables
        //ClipmapCompute.SetVector("ClipmapWorldPosition", ClipmapTransform - (0.5f * volumetricData.ClipmapScale * Vector3.one));
    //    ClipmapCompute.SetFloat("ClipmapScale", volumetricData.ClipmapScale);

        FlipClipBuffer = false;
        //Clear previous capture
        ClipmapCompute.SetTexture(ClearClipmapKernal, "Result", BufferA);
        ClipmapCompute.Dispatch(ClearClipmapKernal, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4);
        ClipmapCompute.SetTexture(ClearClipmapKernal, "Result", BufferB);
        ClipmapCompute.Dispatch(ClearClipmapKernal, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4);


        //Loop through bake texture volumes and put into clipmap //TODO: Add daynamic pass for static unbaked elements
        for (int i = 0; i < VolumetricRegisters.volumetricAreas.Count; i++)
        {
            FlipClipBuffer = !FlipClipBuffer;

            if (FlipClipBuffer)
            {
                ClipmapCompute.SetTexture(ClipmapKernal, "PreResult", BufferB);
                ClipmapCompute.SetTexture(ClipmapKernal, "Result", BufferA);
            }
            else
            {
                ClipmapCompute.SetTexture(ClipmapKernal, "PreResult", BufferA);
                ClipmapCompute.SetTexture(ClipmapKernal, "Result", BufferB);
            }

            //Volumetric variables
            ClipmapCompute.SetTexture(ClipmapKernal, "VolumeMap", VolumetricRegisters.volumetricAreas[i].bakedTexture);
            ClipmapCompute.SetVector("VolumeWorldSize", VolumetricRegisters.volumetricAreas[i].NormalizedScale);
            ClipmapCompute.SetVector("VolumeWorldPosition", VolumetricRegisters.volumetricAreas[i].Corner);

            ClipmapCompute.Dispatch(ClipmapKernal, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4, volumetricData.ClipMapResolution / 4);
        }

        if (FlipClipBuffer)
        {
            SetClipmap(BufferA, volumetricData.ClipmapScale, ClipmapTransform, clipmap);
        }
        else
        {
            SetClipmap(BufferB, volumetricData.ClipmapScale, ClipmapTransform, clipmap);
        }
        
        ClipmapCurrentPos = ClipmapTransform; //Set History
    }
    void SetClipmap(RenderTexture ClipmapTexture, float ClipmapScale, Vector3 ClipmapTransform, Clipmap clipmap)
    {
        Shader.SetGlobalFloat(ClipmapScaleID, ClipmapScale);
        Shader.SetGlobalVector(ClipmapTransformID, ClipmapTransform);

        if (clipmap == Clipmap.Far)
        {
            Shader.SetGlobalTexture("_VolumetricClipmapTexture2", ClipmapTexture);
         //   Debug.Log("Added clipmap far :" + ClipmapTexture.name);
        }
        else
        {        //TODO COMBINE THESE
            Shader.SetGlobalTexture(ClipmapTextureID2, ClipmapTexture); //Set clipmap for
            Shader.SetGlobalTexture(ClipmapTextureID, ClipmapTexture); //Set clipmap for
        }
    }
    #endregion

    bool FlopIntegralBuffer = false;
    void FlopIntegralBuffers(){

        FlopIntegralBuffer = !FlopIntegralBuffer;

        if (FlopIntegralBuffer)
        {
            FroxelFogCompute.SetTexture(ScatteringKernel, "PreviousFrameLighting", FroxelBufferA);
            FroxelFogCompute.SetTexture(ScatteringKernel, "Result", FroxelBufferB);
            FroxelIntegrationCompute.SetTexture(IntegrateKernel, "InLightingTexture", FroxelBufferB);

  //          FroxelIntegrationCompute.SetTexture(IntegrateKernel, "HistoryBuffer", IntegrationBuffer);
   //         FroxelIntegrationCompute.SetTexture(IntegrateKernel, "Result", IntegrationBufferB);

    //        Shader.SetGlobalTexture("_VolumetricResult", IntegrationBufferB);

        }
        else
        {
            FroxelFogCompute.SetTexture(ScatteringKernel, "PreviousFrameLighting", FroxelBufferB);
            FroxelFogCompute.SetTexture(ScatteringKernel, "Result", FroxelBufferA);
            FroxelIntegrationCompute.SetTexture(IntegrateKernel, "InLightingTexture", FroxelBufferA);

    //        FroxelIntegrationCompute.SetTexture(IntegrateKernel, "HistoryBuffer", IntegrationBufferB);
   //         FroxelIntegrationCompute.SetTexture(IntegrateKernel, "Result", IntegrationBuffer);

   //         Shader.SetGlobalTexture("_VolumetricResult", IntegrationBuffer);

        }

        FroxelIntegrationCompute.SetTexture(IntegrateKernel, "HistoryBuffer", IntegrationBuffer);
        FroxelIntegrationCompute.SetTexture(IntegrateKernel, "Result", IntegrationBuffer);
    }

    Matrix4x4 PrevViewProjMatrix = Matrix4x4.identity;

    void SetVariables()
    {
        float extinction = VolumeRenderingUtils.ExtinctionFromMeanFreePath(meanFreePath);

        Shader.SetGlobalFloat("_GlobalExtinction", extinction); //ExtinctionFromMeanFreePath
        Shader.SetGlobalFloat("_StaticLightMultiplier", StaticLightMultiplier); //Global multiplier for static lights
        Shader.SetGlobalVector("_GlobalScattering", extinction * albedo); //ScatteringFromExtinctionAndAlbedo

    }


    void Update()
    {
        CheckOverrideVolumes();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        Matrix4x4 projectionMatrix = Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, volumetricData.far) * Matrix4x4.Rotate(cam.transform.rotation).inverse;
        projectionMatrix = matScaleBias * projectionMatrix ;

        //Previous frame's matrix//!!!!!!!!!


        FroxelFogCompute.SetMatrix(PreviousFrameMatrixID, PreviousFrameMatrix);///
        //   FroxelFogCompute.SetMatrix(PreviousFrameMatrixID, PreviousFrameMatrix );///
        //            var controller = hdCamera.volumeStack.GetComponent<Fog>(); //TODO: Link with controller
        //     UpdateLights();

        CheckClipmap(); // UpdateClipmap();
        FlopIntegralBuffers();
        //  Matrix4x4 lightMatrix = matScaleBias * Matrix4x4.Perspective(LightPosition.spotAngle, 1, 0.1f, LightPosition.range) * Matrix4x4.Rotate(LightPosition.transform.rotation).inverse;
        //TODO: figure out why the meanFreePath has to be so high and fix it. Baking in a large value to compinstate for now.
        VBufferParameters vbuff =  new VBufferParameters(
                                        new Vector3Int(volumetricData.FroxelWidthResolution, volumetricData.FroxelWidthResolution, volumetricData.FroxelDepthResolution), 
                                        volumetricData.far,
                                        cam.nearClipPlane,
                                        cam.farClipPlane,
                                        cam.fieldOfView,
                                        1);

   //     Vector2Int sharedBufferSize = new Vector2Int(volumetricData.FroxelWidthResolution, volumetricData.FroxelHeightResolution); //Taking scaler functuion from HDRP for reprojection
   //     Shader.SetGlobalVector("_VBufferSharedUvScaleAndLimit", vbuff.ComputeUvScaleAndLimit(sharedBufferSize) ); //Just assuming same scale

        Vector4  vres = new Vector4(volumetricData.FroxelWidthResolution, volumetricData.FroxelHeightResolution, 1.0f / volumetricData.FroxelWidthResolution, 1.0f / volumetricData.FroxelHeightResolution);
        //Vector4  vres = new Vector4(cam.pixelWidth, cam.pixelHeight, 1.0f / cam.pixelWidth, cam.pixelHeight);

        Matrix4x4 PixelCoordToViewDirWS = ComputePixelCoordToWorldSpaceViewDirectionMatrix(cam, vres);

        GetHexagonalClosePackedSpheres7(m_xySeq);
        int sampleIndex = Time.renderedFrameCount % 7;

        Shader.SetGlobalVector("_VBufferDistanceEncodingParams", vbuff.depthEncodingParams);
        Shader.SetGlobalVector("_VBufferDistanceDecodingParams", vbuff.depthDecodingParams);
        Shader.SetGlobalMatrix("_VBufferCoordToViewDirWS", PixelCoordToViewDirWS);

        Shader.SetGlobalMatrix("_PrevViewProjMatrix", PrevViewProjMatrix);
        Shader.SetGlobalMatrix("_ViewMatrix", cam.worldToCameraMatrix);


        FroxelFogCompute.SetMatrix(inverseCameraProjectionMatrixID, projectionMatrix.inverse);
        FroxelFogCompute.SetMatrix(Camera2WorldID, cam.transform.worldToLocalMatrix);
        //FroxelFogCompute.SetMatrix("LightProjectionMatrix", lightMatrix);
        //FroxelFogCompute.SetVector("LightPosition", LightPosition.transform.position);
        //FroxelFogCompute.SetVector("LightColor", LightPosition.color * LightPosition.intensity);
        Shader.SetGlobalVector("SeqOffset", new Vector3(m_xySeq[sampleIndex].x, m_xySeq[sampleIndex].y, m_zSeq[sampleIndex] ) ); //Loop through jitters. 
        FroxelFogCompute.SetFloat("reprojectionAmount", reprojectionAmount );

        //jitters[tempjitter]


        Shader.SetGlobalMatrix(CameraProjectionMatrixID,  projectionMatrix);
        Shader.SetGlobalMatrix(TransposedCameraProjectionMatrixID,  projectionMatrix.transpose); //Fragment shaders require the transposed version
        Shader.SetGlobalVector(CameraPositionID, cam.transform.position); //Can likely pack this into the 4th row of the projection matrix 
        Shader.SetGlobalVector(CameraMotionVectorID, cam.transform.position - PreviousCameraPosition); //Extract a motion vector per frame
        Shader.SetGlobalVector("_VolCameraPos", cam.transform.position ); 

        PreviousFrameMatrix = projectionMatrix;
        PreviousCameraPosition = cam.transform.position;
        ////MATRIX
        var gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        PrevViewProjMatrix = gpuProj * cam.worldToCameraMatrix;
//
  //      Debug.Log(PrevViewProjMatrix);
                  //  PreviousFrameMatrix = cam.transformprojectionMatrix * cam.worldToCameraMatrix;

        FroxelFogCompute.Dispatch(ScatteringKernel, (int)ThreadsToDispatch.x, (int)ThreadsToDispatch.y, (int)ThreadsToDispatch.z);
    //    FroxelStackingCompute.DispatchIndirect
        //CONVERT TO DISPATCH INDIRECT to avoid CPU callback?

        FroxelIntegrationCompute.Dispatch(IntegrateKernel, (int)ThreadsToDispatch.x * 2, (int)ThreadsToDispatch.y, (int)ThreadsToDispatch.z); //x2 for stereo

//        BlurCompute.Dispatch(BlurKernelX, (int)ThreadsToDispatch.x * 2, (int)ThreadsToDispatch.y, (int)ThreadsToDispatch.z); // Final blur
//        BlurCompute.Dispatch(BlurKernelY, (int)ThreadsToDispatch.x * 2, (int)ThreadsToDispatch.y, (int)ThreadsToDispatch.z); // Final blur

    }



    //Coping the parms from HDRP to get the log encoded depth.
    struct VBufferParameters 
    {
        public Vector3Int viewportSize;
        public Vector4 depthEncodingParams;
        public Vector4 depthDecodingParams;

        public VBufferParameters(Vector3Int viewportResolution, float depthExtent, float camNear, float camFar, float camVFoV, float sliceDistributionUniformity)
        {
            viewportSize = viewportResolution;

            // The V-Buffer is sphere-capped, while the camera frustum is not.
            // We always start from the near plane of the camera.

            float aspectRatio = viewportResolution.x / (float)viewportResolution.y;
            float farPlaneHeight = 2.0f * Mathf.Tan(0.5f * camVFoV) * camFar;
            float farPlaneWidth = farPlaneHeight * aspectRatio;
            float farPlaneMaxDim = Mathf.Max(farPlaneWidth, farPlaneHeight);
            float farPlaneDist = Mathf.Sqrt(camFar * camFar + 0.25f * farPlaneMaxDim * farPlaneMaxDim);

            float nearDist = camNear;
            float farDist = Mathf.Min(nearDist + depthExtent, farPlaneDist);

            float c = 2 - 2 * sliceDistributionUniformity; // remap [0, 1] -> [2, 0]
            c = Mathf.Max(c, 0.001f);                // Avoid NaNs

            depthEncodingParams = ComputeLogarithmicDepthEncodingParams(nearDist, farDist, c);
            depthDecodingParams = ComputeLogarithmicDepthDecodingParams(nearDist, farDist, c);
        }

        internal Vector4 ComputeUvScaleAndLimit(Vector2Int bufferSize)
        {
            // The slice count is fixed for now.
            return ComputeUvScaleAndLimitFun(new Vector2Int(viewportSize.x, viewportSize.y), bufferSize);
        }

        internal float ComputeLastSliceDistance(int sliceCount)
        {
            float d = 1.0f - 0.5f / sliceCount;
            float ln2 = 0.69314718f;

            // DecodeLogarithmicDepthGeneralized(1 - 0.5 / sliceCount)
            return depthDecodingParams.x * Mathf.Exp(ln2 * d * depthDecodingParams.y) + depthDecodingParams.z;
        }

        // See EncodeLogarithmicDepthGeneralized().
        static Vector4 ComputeLogarithmicDepthEncodingParams(float nearPlane, float farPlane, float c)
        {
            Vector4 depthParams = new Vector4();

            float n = nearPlane;
            float f = farPlane;

            depthParams.y = 1.0f / Mathf.Log(c * (f - n) + 1, 2);
            depthParams.x = Mathf.Log(c, 2) * depthParams.y;
            depthParams.z = n - 1.0f / c; // Same
            depthParams.w = 0.0f;

            return depthParams;
        }

        // See DecodeLogarithmicDepthGeneralized().
        static Vector4 ComputeLogarithmicDepthDecodingParams(float nearPlane, float farPlane, float c)
        {
            Vector4 depthParams = new Vector4();

            float n = nearPlane;
            float f = farPlane;

            depthParams.x = 1.0f / c;
            depthParams.y = Mathf.Log(c * (f - n) + 1, 2);
            depthParams.z = n - 1.0f / c; // Same
            depthParams.w = 0.0f;

            return depthParams;
        }
    }

    internal static float ComputZPlaneTexelSpacing(float planeDepth, float verticalFoV, float resolutionY)
    {
        float tanHalfVertFoV = Mathf.Tan(0.5f * verticalFoV);
        return tanHalfVertFoV * (2.0f / resolutionY) * planeDepth;
    }

    internal static Vector4 ComputeUvScaleAndLimitFun(Vector2Int viewportResolution, Vector2Int bufferSize)
    {
        Vector2 rcpBufferSize = new Vector2(1.0f / bufferSize.x, 1.0f / bufferSize.y);

        // vp_scale = vp_dim / tex_dim.
        Vector2 uvScale = new Vector2(viewportResolution.x * rcpBufferSize.x,
                                      viewportResolution.y * rcpBufferSize.y);

        // clamp to (vp_dim - 0.5) / tex_dim.
        Vector2 uvLimit = new Vector2((viewportResolution.x - 0.5f) * rcpBufferSize.x,
                                      (viewportResolution.y - 0.5f) * rcpBufferSize.y);

        return new Vector4(uvScale.x, uvScale.y, uvLimit.x, uvLimit.y);
    }



    private void OnEnable()
    {
        Shader.EnableKeyword("_VOLUMETRICS_ENABLED");
#if UNITY_EDITOR
        if (!Application.isPlaying) return;
#endif
        Intialize();
    }
    private void OnDisable() //Disable this if we decide to just pause rendering instead of removing. 
    {
        ReleaseAssets();
    }
    private void OnDestroy()
    {
        ReleaseAssets();
    }


    Matrix4x4 ComputePixelCoordToWorldSpaceViewDirectionMatrix(Camera cam, Vector4 resolution)
    {
        //   var proj = cam.projectionMatrix; //  GL.GetGPUProjectionMatrix(cameraProj, true); //Use this if we run into platform issues
        //bandaid fix. There's an issue with the far clip plane in the matrix projection. 
        var proj = Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, 100000f); 
        var view = cam.worldToCameraMatrix ;

        var invViewProjMatrix = (proj * view).inverse;

        var transform = Matrix4x4.Scale(new Vector3(-1.0f, -1.0f, -1.0f)) * invViewProjMatrix; // (gpuProj * gpuView).inverse
     //   transform = transform * Matrix4x4.Scale(new Vector3(1.0f, -1.0f, 1.0f));
        transform = transform * Matrix4x4.Translate(new Vector3(-1.0f, -1.0f, 0.0f));
        transform = transform * Matrix4x4.Scale(new Vector3(2.0f * resolution.z, 2.0f * resolution.w, 1.0f)) ;

        return transform.transpose;
    }


    void SetComputeVariables()
    {


    }

    /// <summary>
    /// Editor
    /// </summary>
    /// 
    private void OnDrawGizmosSelected()
    {
        if (cam == null || volumetricData == null) return;

        Gizmos.color = Color.black;
;
        Gizmos.matrix = Matrix4x4.TRS(cam.transform.position, cam.transform.rotation, Vector3.one);
        Gizmos.DrawFrustum(Vector3.zero, cam.fieldOfView, volumetricData.near, volumetricData.far, cam.aspect);

        Gizmos.color = Color.cyan;
        Gizmos.matrix = Matrix4x4.TRS(ClipmapCurrentPos, Quaternion.identity, Vector3.one * volumetricData.ClipmapScale);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);

        Gizmos.color = Color.blue;
        Gizmos.matrix = Matrix4x4.TRS(ClipmapCurrentPos, Quaternion.identity, Vector3.one * volumetricData.ClipmapScale2);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);


    }

    void ReleaseAssets()
    {
        Shader.DisableKeyword("_VOLUMETRICS_ENABLED");
        if (ClipmapBufferA!= null) ClipmapBufferA.Release();
        if (FroxelBufferA != null) FroxelBufferA.Release();   
        if (IntegrationBuffer != null)IntegrationBuffer.Release();
    }

#if UNITY_EDITOR

    void assignVaris()
    {
        cam = GetComponentInChildren<Camera>();
        //Get shaders and seri
        if (FroxelFogCompute == null)
            FroxelFogCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.universal/Shaders/Volumetrics/VolumetricScattering.compute");
        if (FroxelIntegrationCompute == null)
            FroxelIntegrationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.universal/Shaders/Volumetrics/StepAdd.compute");
        if (ClipmapCompute == null)
            ClipmapCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.universal/Shaders/Volumetrics/ClipMapGenerator.compute");
        if (BlurCompute == null)
            BlurCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.universal/Shaders/Volumetrics/VolumetricBlur.compute");
    }


    private void Reset()
    {
        assignVaris();
    }
#endif

    private void OnValidate()
    {
#if UNITY_EDITOR
        //Black Texture in editor to not get in the way. Isolated h ere because shaders should skip volumetric tex in precompute otherwise. 
        // TODO: Add proper scene preview feature
         if (!UnityEditor.EditorApplication.isPlaying && BlackTex == null ) BlackTex = (Texture3D)MakeBlack3DTex();
        //        UnityEditor.SceneManagement.EditorSceneManager.sceneUnloaded += UnloadKeyword; //adding function when scene is unloaded 
        assignVaris();
#endif
        //if (cam == null) cam = GetComponent<Camera>();
        //if (volumetricData.near < cam.nearClipPlane || volumetricData.far > cam.farClipPlane)
        //{
        //    //Auto clamp to inside of the camera's clip planes
        //    volumetricData.near = Mathf.Max(volumetricData.near, cam.nearClipPlane);
        //    volumetricData.far = Mathf.Min(volumetricData.far, cam.farClipPlane);
        //}

        Shader.EnableKeyword("_VOLUMETRICS_ENABLED"); //enabling here so the editor knows that it exists
    }

    Texture MakeBlack3DTex()
    {
        Debug.Log("Made blank texture");

        int size = 1;

        Texture3D BlackTex = new Texture3D(1, 1, 1, TextureFormat.ARGB32, false);
        var cols = new Color[size * size * size];
        float mul = 1.0f / (size - 1);
        int idx = 0;
        Color c = Color.white;
        for (int z = 0; z < size; ++z)
        {
            for (int y = 0; y < size; ++y)
            {
                for (int x = 0; x < size; ++x, ++idx)
                {
                    c.r = 0;
                    c.g = 0;
                    c.b = 0;
                    cols[idx] = c;
                }
            }
        }

        BlackTex.SetPixels(cols);
        BlackTex.Apply();
        // SetClipmap(BlackTex, 50, Vector3.zero);

        Shader.SetGlobalTexture("_VolumetricResult", BlackTex);

        //    Shader.SetGlobalTexture("_VolumetricClipmapTexture", BlackTex); //Set clipmap for
        return BlackTex;
    }


    //public void UnloadKeyword<Scene>(Scene scene)
    //{
    //    Shader.DisableKeyword("_VOLUMETRICS_ENABLED");

    //    print("The scene was unloaded!");
    //}


}
