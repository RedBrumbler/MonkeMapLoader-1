﻿using BepInEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using VmodMonkeMapLoader.Helpers;
using VmodMonkeMapLoader.Models;
using Logger = VmodMonkeMapLoader.Helpers.Logger;
using Object = UnityEngine.Object;

namespace VmodMonkeMapLoader.Behaviours
{
    public class MapLoader : MonoBehaviour
    {
        private static GameObject _mapInstance;
        private static bool _isLoading;
        private static GlobalData _globalData;
        private static MapDescriptor _descriptor;

        public static string _lobbyName;
        
        private void Awake()
        {
            InitializeGlobalData();
        }

        public static void ForceRespawn()
        {
            Teleporter treeTeleporter = _globalData.BigTreeTeleportToMap.GetComponent<Teleporter>();

            treeTeleporter.TeleportPoints = GameObject.Find("SpawnPointContainer")?.GetComponentsInChildren<Transform>().ToList();

            var destination = treeTeleporter.TeleportPoints.Count > 1
            ? treeTeleporter.TeleportPoints[UnityEngine.Random.Range(0, treeTeleporter.TeleportPoints.Count)]
            : treeTeleporter.TeleportPoints[0];

            Logger.LogText("Teleporting due to round end");

            Patches.PlayerTeleportPatch.TeleportPlayer(destination);
            
        }

        public static void ColorTreeTeleporter(Color color)
        {
            foreach(Renderer renderer in _globalData.BigTreeTeleportToMap.GetComponentsInChildren<Renderer>())
            {
                foreach(Material mat in renderer.sharedMaterials)
                {
                    if (mat.name.Contains("Center")) mat.SetColor("_Color", color);
                }
            }
        }
        
        public static void JoinGame()
        {
            if (!_lobbyName.IsNullOrWhiteSpace())
            {
                Utilla.Utils.RoomUtils.JoinModdedLobby(_lobbyName);
                if(_descriptor != null && _descriptor.GravitySpeed != -9.8f)
                {
                    Physics.gravity = new Vector3(0, _descriptor.GravitySpeed, 0);
                }
            }
        }

        public static void ResetMapProperties()
        {
            if (Physics.gravity.y != -9.8f) Physics.gravity = new Vector3(0, -9.8f, 0);
        }

        public void LoadMap(MapInfo mapInfo, Action<bool> isSuccess)
        {
            StartCoroutine(LoadMapFromPackageFileAsync(mapInfo, b =>
            {
                Debug.Log("______ MAP LOADED");
                isSuccess(b);
            }));
        }
        
        public IEnumerator LoadMapFromPackageFileAsync(MapInfo mapInfo, Action<bool> isSuccess)
        {
            if (_isLoading)
            {
                yield break;
            }

            _isLoading = true;

            UnloadMap();
            Logger.LogText("Loading map: " + mapInfo.FilePath + " -> " + mapInfo.PackageInfo.Descriptor.Name);

            var bundleDataStream = MapFileUtils.GetMapDataStreamFromZip(mapInfo);
            if (bundleDataStream == null)
            {
                Logger.LogText("Bundle not found in package");

                _isLoading = false;
                isSuccess(false);
                yield break;
            }

            var loadBundleRequest = AssetBundle.LoadFromStreamAsync(bundleDataStream);
            yield return loadBundleRequest;

            var bundle = loadBundleRequest.assetBundle;
            if (bundle == null)
            {
                Logger.LogText("Bundle NOT LOADED");

                _isLoading = false;
                isSuccess(false);
                yield break;
            }

            Logger.LogText("Bundle loaded");

            var assetNames = bundle.GetAllAssetNames();

            Logger.LogText("Asset count: " + assetNames.Length + ", assets: " + string.Join(";", assetNames));
            
            Logger.LogText("Asset name: " + mapInfo.PackageInfo.Descriptor.Name);

            string[] scenePath = bundle.GetAllScenePaths();

            if (scenePath.Length <= 0)
            {
                Logger.LogText("Bundle NOT LOADED");

                _isLoading = false;
                isSuccess(false);
                yield break;
            }

            var scene = SceneManager.LoadSceneAsync(scenePath[0], LoadSceneMode.Additive);
            yield return scene;

            GameObject[] allObjects = FindObjectsOfType<GameObject>();
            MapDescriptor descriptor = FindObjectOfType<MapDescriptor>();

            foreach(GameObject gameObject in allObjects)
            {
                if (gameObject.scene.name != "GorillaTagNewVisuals" && gameObject.scene.name != "DontDestroyOnLoad")
                {
                    if(gameObject.transform.parent == null & gameObject.transform != descriptor.transform)
                    {
                        gameObject.transform.SetParent(descriptor.transform);
                    }
                }
            }
            GameObject map = descriptor.gameObject;

            if (map == null)
            {
                _isLoading = false;
                isSuccess(false);
                bundle.Unload(false);
                yield break;
            }

            Logger.LogText("Map asset loaded: " + map.name);
            _lobbyName = mapInfo.PackageInfo.Descriptor.Author + "_" + mapInfo.PackageInfo.Descriptor.Name;

            Exception ex = null;

            yield return ProcessAndInstantiateMap(map).AsIEnumerator(exception => ex = exception);
            yield return null;

            if (ex != null)
            {
                Logger.LogException(ex);

                isSuccess(false);
                _isLoading = false;
                bundle.Unload(false);
                yield break;
            }

            bundle.Unload(false);
            bundleDataStream.Close();

            _isLoading = false;
            isSuccess(true);
        }

        public void UnloadMap()
        {
            if (_mapInstance != null)
            {
                Logger.LogText("Destroying old map");

                Destroy(_mapInstance);

                _mapInstance = null;
                Resources.UnloadUnusedAssets();
            }
        }

        private async Task ProcessAndInstantiateMap(GameObject map)
        {
            await Task.Factory.StartNew(() =>
            {
                Logger.LogText("Instantiate map");

                _mapInstance = map;
                _descriptor = _mapInstance?.GetComponent<MapDescriptor>();

                ProcessChildObjects(map);

                Transform fakeSkybox = _mapInstance.transform.Find("FakeSkybox");
                if (fakeSkybox != null)
                {
                    Material oldMat = fakeSkybox.GetComponent<Renderer>().material; 
                    if (oldMat.HasProperty("_Tex"))
                    {
                        oldMat.SetTexture("_Tex", Resources.Load<Texture2D>("objects/forest/materials/sky"));
                        oldMat.SetColor("_Color", new Color(1, 1, 1, 1));
                    }
                }

                GameObject FallEmergencyTeleport = new GameObject("FallEmergencyTeleport");
                FallEmergencyTeleport.layer = Constants.MaskLayerHandTrigger;
                FallEmergencyTeleport.AddComponent<BoxCollider>().isTrigger = true;
                FallEmergencyTeleport.transform.SetParent(_mapInstance.transform);
                FallEmergencyTeleport.transform.localScale = new Vector3(2000f, 1f, 2000f);
                FallEmergencyTeleport.transform.localPosition = new Vector3(0f, -300f, 0f);

                Teleporter emergencyFallTeleporter = FallEmergencyTeleport.AddComponent<Teleporter>();
                emergencyFallTeleporter.TeleportPoints = _mapInstance.GetComponent<MapDescriptor>().SpawnPoints.ToList();
                emergencyFallTeleporter.TagOnTeleport = true;
                emergencyFallTeleporter.TeleporterType = TeleporterType.Map;
            });
        }

        private void ProcessChildObjects(GameObject parent)
        {
            if (parent == null) return;
            // Logger.LogText("Processing parent: " + parent.name + ", childs: " + parent.transform.childCount);

            for (var i = 0; i < parent.transform.childCount; i++)
            {
                var child = parent.transform.GetChild(i).gameObject;

                // Logger.LogText("Processing object: " + child.name);

                //FixShader(child); <- this crashes for some reason.

                SetupCollisions(child);

                if (child.transform.childCount > 0)
                {
                    ProcessChildObjects(child);
                }
            }

            foreach(Teleporter teleporter in parent.gameObject.GetComponentsInChildren<Teleporter>())
            {
                if (teleporter.TeleportPoints == null || teleporter.TeleportPoints.Count == 0)
                {
                    teleporter.TeleportPoints = new List<Transform>() { _globalData.BigTreePoint.transform };
                    teleporter.TeleporterType = TeleporterType.Treehouse;
                }
            }
        }

        private void SetupCollisions(GameObject child)
        {
            if (child == null) return;
            var colliders = child.GetComponents<Collider>();
            if (colliders == null) return;
            foreach (var collider in colliders)
            {
                if (collider == null) return;
                if (collider.isTrigger)
                {
                    child.layer = Constants.MaskLayerGorillaTrigger;
                    break;
                }else if(child != null && child.layer == 0)
                {
                    child.layer = 9;
                }
                if (child.GetComponent<Teleporter>() != null || child.GetComponent<TagZone>() != null)
                {
                    collider.isTrigger = true;
                    child.layer = Constants.MaskLayerHandTrigger;
                }
            }
        }

        private void InitializeGlobalData()
        {
            if(_globalData == null)
                _globalData = new GlobalData();

            _globalData.Origin = GameObject.Find("slide")?.transform.position ?? Vector3.zero;

            _globalData.TreeOrigin = GameObject.Find("stool")?.transform.position ?? Vector3.zero;
            
            // Tree Teleport Stuff
            if (_globalData.BigTreeTeleportToMap == null)
            {
                var teleporterPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(MapFileUtils).Assembly.Location), Constants.MiscObjectsFolderName, "Teleporter");
                AssetBundle bundle = MapFileUtils.GetAssetBundleFromZip(teleporterPath);
                _globalData.BigTreeTeleportToMap = Object.Instantiate(bundle.LoadAsset<GameObject>("_Teleporter"));

                _globalData.BigTreeTeleportToMap.layer = Constants.MaskLayerPlayerTrigger;
                DontDestroyOnLoad(_globalData.BigTreeTeleportToMap);
            }


            var orbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(MapFileUtils).Assembly.Location), Constants.MiscObjectsFolderName, "Orb");
            AssetBundle orbBundle = MapFileUtils.GetAssetBundleFromZip(orbPath);

            GameObject orb = Object.Instantiate(orbBundle.LoadAsset<GameObject>("_Orb"));
            GameObject orbVisuals = Object.Instantiate(orb);

            orb.AddComponent<RotateByHand>();
            orb.GetComponent<Renderer>().enabled = false;
            Destroy(orb.GetComponent<Renderer>());
            orb.layer = 18;

            orbVisuals.transform.SetParent(orb.transform);
            orbVisuals.transform.localPosition = Vector3.zero;
            orbVisuals.transform.localScale = Vector3.one;
            orbVisuals.transform.localRotation = Quaternion.identity;

            orbVisuals.AddComponent<PreviewOrb>();
            orbVisuals.GetComponent<Collider>().enabled = false;
            Destroy(orbVisuals.GetComponent<Collider>());

            //orb.layer = Constants.MaskLayerPlayerTrigger;

            Teleporter treeTeleporter = _globalData.BigTreeTeleportToMap.GetComponent<Teleporter>();
            treeTeleporter.TeleporterType = TeleporterType.Map;
            treeTeleporter.JoinGameOnTeleport = true;
            treeTeleporter.TeleportPoints = new List<Transform>();
            treeTeleporter.Delay = 1.5f;
            treeTeleporter.TouchType = GorillaTouchType.Head;

            DontDestroyOnLoad(treeTeleporter);
            if(_globalData.BigTreePoint == null)
            {
                _globalData.BigTreePoint = new GameObject("TreeHomeTargetObject");
                _globalData.BigTreePoint.transform.position = new Vector3(-66f, 12.3f, -83f);
                _globalData.BigTreePoint.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
                DontDestroyOnLoad(_globalData.BigTreePoint);
            }
            treeTeleporter.TeleportPoints.Add(_globalData.BigTreePoint.transform);

            ColorTreeTeleporter(new Color(0, 1, 0));

            // Emergency Teleport Stuff
            if (_globalData.FallEmergencyTeleport != null)
            {
                Destroy(_globalData.FallEmergencyTeleport);
                _globalData.FallEmergencyTeleport = null;
            }
            _globalData.FallEmergencyTeleport = new GameObject("FallEmergencyTeleport");
            _globalData.FallEmergencyTeleport.layer = Constants.MaskLayerHandTrigger;
            _globalData.FallEmergencyTeleport.AddComponent<BoxCollider>().isTrigger = true;
            _globalData.FallEmergencyTeleport.transform.localScale = new Vector3(1000f, 1f, 1000f);
            _globalData.FallEmergencyTeleport.transform.position = _globalData.TreeOrigin + new Vector3(0f, -200f, 0f);
            DontDestroyOnLoad(_globalData.FallEmergencyTeleport);

            Teleporter emergencyFallTeleporter = _globalData.FallEmergencyTeleport.AddComponent<Teleporter>();
            emergencyFallTeleporter.TeleportPoints = new List<Transform>() { _globalData.BigTreePoint.transform };
        }
    }
}