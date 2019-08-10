using UnityEngine;
using GraphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat;
using IntPtr = System.IntPtr;
using TimeSpan = System.TimeSpan;
using System.Threading;
using Microsoft.Azure.Kinect.Sensor;

namespace Akvfx
{
    public sealed class PointCloudBaker : MonoBehaviour
    {
        #region Editable attributes

        [SerializeField] RenderTexture _colorTexture = null;
        [SerializeField] RenderTexture _positionTexture = null;
        [SerializeField, HideInInspector] Shader _shader = null;

        #endregion

        #region K4a objects

        Device _device;
        Transformation _transformation;

        #endregion

        #region UnityEngine objects

        Material _material;
        Texture2D _colorTemp;
        Texture2D _pointCloud;

        #endregion

        #region Threaded capture

        Thread _captureThread;
        bool _terminate;
        AutoResetEvent _nextCapture;
        (Image color, Image pointCloud) _captured;

        void CaptureThread()
        {
            while (!_terminate)
            {
                try
                {
                    // Try to capture a frame with 1/15 sec timeout.
                    using (var capture = _device.GetCapture(TimeSpan.FromSeconds(1.0 / 15)))
                    {
                        // Transform the depth image to the color perspective.
                        using (var depth = _transformation.DepthImageToColorCamera(capture))
                        {
                            // Unproject the depth samples and reconstruct a point cloud.
                            using (var pointCloud = _transformation.DepthImageToPointCloud
                                (depth, CalibrationDeviceType.Color))
                            {
                                // Send the results to the main thread.
                                _captured = (capture.Color, pointCloud);

                                // Wait the main thead consumes them.
                                _nextCapture.WaitOne();
                            }
                        }
                    }
                }
                catch (System.TimeoutException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        #endregion

        #region Private functions

        unsafe void UpdateTexturesByCapturedData()
        {
            var cmem = _captured.color.Memory;

            using (var handle = cmem.Pin())
                _colorTemp.LoadRawTextureData((IntPtr)handle.Pointer, cmem.Length);

            var pmem = _captured.pointCloud.Memory;

            using (var handle = pmem.Pin())
                _pointCloud.LoadRawTextureData((IntPtr)handle.Pointer, pmem.Length);

            _colorTemp.Apply();
            _pointCloud.Apply();

            _material.SetTexture("_SourceTexture", _pointCloud);
            _material.SetVector("_Dimensions", new Vector2(
                _captured.color.WidthPixels,
                _captured.color.HeightPixels
            ));

            Graphics.Blit(_colorTemp, _colorTexture);
            Graphics.Blit(null, _positionTexture, _material, 0);
        }

        #endregion

        #region MonoBehaviour implementation

        void Start()
        {
            // If there is no available device, do nothing.
            if (Device.GetInstalledCount() == 0) return;

            // Open the default device.
            _device = Device.Open();
            if (_device == null) return;

            // Start capturing with our settings.
            _device.StartCameras(
                new DeviceConfiguration {
                    ColorFormat = ImageFormat.ColorBGRA32,
                    ColorResolution = ColorResolution.R1536p, // 2048 x 1536 (4:3)
                    DepthMode = DepthMode.NFOV_Unbinned,      // 640x576
                    SynchronizedImagesOnly = true
                }
            );

            // Prepare the transformation object.
            _transformation = new Transformation(_device.GetCalibration());

            // Temporary objects for convertion shader
            _material = new Material(_shader);
            _colorTemp = new Texture2D(2048, 1536, GraphicsFormat.B8G8R8A8_SRGB, 0);
            _pointCloud = new Texture2D(2048 * 6, 1536, GraphicsFormat.R8_UNorm, 0);

            // Start the capture thread.
            _captureThread = new Thread(CaptureThread);
            _nextCapture = new AutoResetEvent(false);
            _captureThread.Start();
        }

        void OnDestroy()
        {
            if (_captureThread != null)
            {
                _terminate = true;
                _nextCapture.Set();
                _captureThread.Join();
            }

            if (_material != null) Destroy(_material);
            if (_colorTemp != null) Destroy(_colorTemp);
            if (_pointCloud != null) Destroy(_pointCloud);

            _transformation?.Dispose();
            _device?.StopCameras();
            _device?.Dispose();
        }

        void Update()
        {
            if (_captured.color != null && _captured.pointCloud != null)
            {
                UpdateTexturesByCapturedData();
                _captured = (null, null);
                _nextCapture.Set();
            }
        }

        #endregion
    }
}