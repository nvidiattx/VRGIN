﻿using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using VRGIN.Helpers;

namespace VRGIN.Core
{
    public class InitializeCameraEventArgs : EventArgs
    {
        public readonly Camera Camera;
        public readonly Camera Blueprint;

        public InitializeCameraEventArgs(Camera camera, Camera blueprint)
        {
            Camera = camera;
            Blueprint = blueprint;
        }
    }

    /// <summary>
    /// Handles the insertion of a OpenVR camera into an existing scene. The camera is controlled by a ControlMode.
    /// </summary>
    public class VRCamera : ProtectedBehaviour
    {
        private delegate void CameraOperation(Camera camera);

        private static VRCamera _Instance;
        public SteamVR_Camera SteamCam { get; private set; }
        public Camera Blueprint { get; private set; }
        private RenderTexture _MiniTexture;
        public bool HasValidBlueprint { get; private set; }

        public Transform Origin
        {
            get
            {
                return SteamCam.origin;
            }
        }

        public Transform Head
        {
            get
            {
                return SteamCam.head;
            }
        }
        /// <summary>
        /// Called when a camera is being initialized.
        /// </summary>
        public event EventHandler<InitializeCameraEventArgs> InitializeCamera = delegate { };

        /// <summary>
        /// Gets the current instance of VRCamera or creates one if need be.
        /// </summary>
        public static VRCamera Instance
        {
            get
            {
                if (_Instance == null)
                {
                    _Instance = new GameObject("VRGIN_Camera").AddComponent<AudioListener>().gameObject.AddComponent<VRCamera>();
                }
                return _Instance;
            }
        }

        protected override void OnAwake()
        {
            // Dummy texture to let the old main camera render to
            _MiniTexture = new RenderTexture(1, 1, 0);
            _MiniTexture.Create();

            gameObject.AddComponent<SteamVR_Camera>();
            SteamCam = GetComponent<SteamVR_Camera>();
            SteamCam.Expand(); // Expand immediately!

            if (!VR.Settings.MirrorScreen)
            {
                Destroy(SteamCam.head.GetComponent<SteamVR_GameView>());
                Destroy(SteamCam.head.GetComponent<Camera>()); // Save GPU power
            }

            // Set render scale to the value defined by the user
            SteamVR_Camera.sceneResolutionScale = VR.Settings.RenderScale;

            // Needed for the Camera Modifications mod to work. It's an artifact from DK2 days
            var legacyAnchor = new GameObject("CenterEyeAnchor");
            legacyAnchor.transform.SetParent(SteamCam.head);

            DontDestroyOnLoad(SteamCam.origin.gameObject);
        }

        /// <summary>
        /// Copies the values of a in-game camera to the VR camera.
        /// </summary>
        /// <param name="blueprint">The camera to copy.</param>
        public void Copy(Camera blueprint)
        {
            VRLog.Info("Copying camera: {0}", blueprint ? blueprint.name : "NULL");
            Blueprint = blueprint ?? GetComponent<Camera>();

            int cullingMask = Blueprint.cullingMask;

            // If the camera is blind, set it to see everything instead
            if (cullingMask == 0)
            {
                cullingMask = int.MaxValue;
            }
            else
            {
                // Apply additional culling masks
                foreach (var subCamera in VR.Interpreter.FindSubCameras())
                {
                    if (!subCamera.name.Contains(SteamCam.baseName))
                    {
                        cullingMask |= subCamera.cullingMask;
                    }
                }
            }

            // Remove layers that are captured by other cameras (see VRGUI)
            cullingMask |= LayerMask.GetMask("Default");
            cullingMask &= ~(LayerMask.GetMask(VR.Context.UILayer, VR.Context.InvisibleLayer));
            cullingMask &= ~(VR.Context.IgnoreMask);
            VRLog.Info("The camera sees {0}", string.Join(", ", UnityHelper.GetLayerNames(cullingMask)));

            // Apply to both the head camera and the VR camera
            ApplyToCameras(targetCamera =>
            {
                targetCamera.nearClipPlane = 0.01f;
                targetCamera.farClipPlane = Blueprint.farClipPlane;
                targetCamera.cullingMask = cullingMask;
                targetCamera.clearFlags = Blueprint.clearFlags == CameraClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;

                targetCamera.backgroundColor = Blueprint.backgroundColor;

                var skybox = Blueprint.GetComponent<Skybox>();
                if (skybox != null)
                {
                    var vrSkybox = targetCamera.gameObject.GetComponent<Skybox>();
                    if (vrSkybox == null) vrSkybox = vrSkybox.gameObject.AddComponent<Skybox>();

                    vrSkybox.material = skybox.material;
                }

                // Hook
                InitializeCamera(this, new InitializeCameraEventArgs(targetCamera, Blueprint));
            });

            // Only execute this code when the blueprint is a different camera
            HasValidBlueprint = Blueprint != GetComponent<Camera>();
            if (HasValidBlueprint)
            {
                //StartCoroutine(ExecuteDelayed(delegate { CopyFX(Blueprint); }));
                //CopyFX(Blueprint);

                Blueprint.cullingMask = 0;
                Blueprint.nearClipPlane = Blueprint.farClipPlane = 0;

                //Blueprint.targetTexture = _MiniTexture;
                //Blueprint.gameObject.AddComponent<BlacklistThrottler>();

                // Highlander principle
                var listener = Blueprint.GetComponent<AudioListener>();
                if (listener)
                {
                    Destroy(listener);
                }
            }
        }

        private IEnumerator ExecuteDelayed(Action action)
        {
            yield return null;
            try
            {
                action();
            }
            catch (Exception e)
            {
                VRLog.Error(e);
            }
        }

        /// <summary>
        /// Doesn't really work yet.
        /// </summary>
        /// <param name="blueprint"></param>
        public void CopyFX(Camera blueprint)
        {

            CopyFX(blueprint.gameObject, gameObject, true);

            if (!SteamCam)
            {
                SteamCam = GetComponent<SteamVR_Camera>();
            }
            SteamCam.ForceLast();
            SteamCam = GetComponent<SteamVR_Camera>();
        }

        private void CopyFX(GameObject source, GameObject target, bool disabledSourceFx = false)
        {
            // Clean
            foreach (var fx in target.GetCameraEffects())
            {
                DestroyImmediate(fx);
            }
            int comps = target.GetComponents<Component>().Length;

            VRLog.Info("Copying FX to {0}...", target.name);
            // Rebuild
            foreach (var fx in source.GetCameraEffects())
            {
                if (VR.Interpreter.IsAllowedEffect(fx))
                {
                    VRLog.Info("Copy FX: {0} (enabled={1})", fx.GetType().Name, fx.enabled);
                    var attachedFx = target.CopyComponentFrom(fx);
                    if (attachedFx)
                    {
                        VRLog.Info("Attached!");
                    }
                    attachedFx.enabled = fx.enabled;
                } else
                {
                    VRLog.Info("Skipping image effect {0}", fx.GetType().Name);
                }

                if (disabledSourceFx)
                {
                    fx.enabled = false;
                }
            }
            VRLog.Info("{0} components before the additions, {1} after", comps, target.GetComponents<Component>().Length);
        }

        private void ApplyToCameras(CameraOperation operation)
        {
            operation(SteamCam.GetComponent<Camera>());
            //operation(SteamCam.head.GetComponent<Camera>());
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (SteamCam.origin)
            {
                // Make sure the scale is right
                SteamCam.origin.localScale = Vector3.one * VR.Settings.IPDScale;
            }
        }

        public void Refresh()
        {
            CopyFX(Blueprint);
        }

        internal Camera Clone(bool copyEffects = true)
        {
            var clone = new GameObject("VRGIN_Camera_Clone").CopyComponentFrom(SteamCam.GetComponent<Camera>());

            if (copyEffects)
            {
                CopyFX(SteamCam.gameObject, clone.gameObject);
            }
            clone.transform.position = transform.position;
            clone.transform.rotation = transform.rotation;
            clone.nearClipPlane = 0.01f;

            return clone;
        }
    }
}
