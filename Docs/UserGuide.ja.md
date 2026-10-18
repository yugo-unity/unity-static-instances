# Static Instances ユーザーガイド

このガイドでは、Static Instance Scatter (SIS) をシーンに導入する手順、最初に選ぶべき構成、
ランタイムで使える制御を説明します。対応する Unity/パッケージのバージョンとインストール方法は
[README](../README.ja.md#動作要件) を参照してください。

## GUI リファレンス

詳細な設定は、編集を行うウィンドウごとに整理しています。

| ページ | 用途 |
| --- | --- |
| [Static Instance Scatter Inspector](StaticInstanceScatterInspector.ja.md) | 1 つの SIS オーナーの可視性、シャドウ、メッシュ LOD、Draw Range、フェードを設定します。 |
| [Static Instances Graphics Settings](GraphicsSettings.ja.md) | プロジェクト全体の品質、自動ベイク、ストリッピングのポリシーを設定します。 |
| [Static Instances オペレーションウィンドウ](OperationsWindow.ja.md) | 検証、ベイク、LODGroup の変換、保存済みシーンの処理を行います。 |

## シーンへの導入

1. 不透明で BRG/DOTS インスタンシング互換の MeshRenderer を使用します。静的なソース
   オブジェクトを、**Rendering > Static Instance Scatter** を付けた親の下に配置します。
   各 MeshRenderer の **Batching Static** フラグはオフのままにします。SIS オーナーは
   入れ子にしないでください。
2. シーンを保存します。**Window > Rendering > Static Instances** を開くか、
   **Project Settings > Graphics > Static Instances** の **Open Operations Window** を使います。
3. **Validate** を実行し、報告された未サポートのコンテンツをベイク前に解消します。
4. 保存済みシーンの行を選択し、**Bake Selected Scenes** を実行します。
   この操作は、選択した各シーンについて検証、ベイク、風を取り除いた SpeedTree
   マテリアルの生成、保存を順に行います。
5. Play に入って結果を確認します。SIS がコンテンツを所有している間、通常のソース
   レンダラーは抑制されます。オーサリング用にソース階層は残しておいてください。

インスタンスの Transform、メッシュ、マテリアル、レンダリング属性は初期化後は静的です。
ソースのジオメトリを移動・差し替えた場合は、再ベイクと再初期化が必要です。Transform を
編集しても、初期化済みのインスタンスバッファーは更新されません。可視性、シャドウ、
LOD の制御には、後述のランタイム API があります。

### Player におけるコンポーネントのライフタイム

オーサリング用のソースシーンは残しておいてください。**Bake On Build** は、ビルド時の
シーンのコピー上で古くなったコンテンツを再ベイクします。オフにする場合は、ビルド前に
再ベイクしてください。

ビルドプロセッサーがこのオーナーのソースをストリップした Player では、初期化時に
準備済みの行列をアップロードし、その CPU 側のストレージを解放します。コンポーネントまたは
その GameObject を無効にすると、GPU とカリングのリソースを保持したまま SIS の描画が停止し、
再度有効にするとそれらを再利用します。Destroy/アンロードでこれらのリソースは解放されます。
スクリプトを持つソースレンダラーが残っている場合、それらは抑制されたままです。Editor と
ソースを保持するビルドでは、無効化/破棄/ソース復元のライフサイクルになります。

`GetBakedData` は明示的に呼び出すたびに新しい Matrix4x4 配列を再構築し、ストリップ済みの
Player が行列ストレージを解放した後は false を返します。デコードせずにメタデータを
取得するには `BakedInstanceCount` と `GetBakedDrawGroups()` を使います。

### 既存の LODGroup コンテンツ

SIS は LODGroup を直接描画しません。**Convert LOD Groups…** を実行してから
**Generate Mesh LODs…** を実行し、それぞれのプレビューを確認してから適用してください。
LOD の指標を選ぶだけではメッシュのレベルは作成されません。変換の対象範囲、アセットへの
変更、Undo の制限については[オペレーションウィンドウのリファレンス](OperationsWindow.ja.md)
を参照してください。

## LOD の選択方式とフェードは別々に選ぶ

LOD は**既定で無効**です。メッシュに必要なメッシュ LOD レベルがある場合は有効にし、
まず **Screen Size** による選択と **Time** フェードから始めてください。

- 投影サイズに応じてディテールを割り当てるなら **Screen Size**、遷移境界をメートルで
  オーサリングするなら **Distance** を選びます。
- 時間をかけて遷移を完了させるなら **Time**、カメラ位置に追従してブレンドするなら
  **Distance**、即時に切り替えるなら **None** を選びます。

この 2 つは独立した選択です。フェードを使うには、プロジェクトの **Fade > Enabled** と
URP Asset の **LOD Cross Fade** も必要です。単位、ラダー、LOD の強制、実効モードについては
[SIS Inspector リファレンス](StaticInstanceScatterInspector.ja.md)を、プロジェクトの
品質オーバーライドについては [Graphics Settings](GraphicsSettings.ja.md) を参照してください。

## 可視性、Draw Range、サイズベースのカリング

**Visible** はシャドウを含むオーナーの描画を制御します。**Cast Shadows** はカメラ描画を
有効にしたまま、シャドウのみを無効にできます。**Draw Range** はメッシュ LOD が無効でも
機能します。距離の上限が必要な場合は、まず Max を明示的に指定してください。0 は
オーサリングによる遠方の制限なしを意味します。Distance と Screen Size では距離の扱いが
異なるため、境界を正確にオーサリングする前に
[SIS Inspector](StaticInstanceScatterInspector.ja.md) を確認してください。

サイズに基づいて除去するには、Screen Size のオーナーでは **Culled** のしきい値を、
Distance モードのオーナーではプロジェクトの **Cull Distance Scale** を使います。詳細な
挙動と Draw Range との相互作用は [Graphics Settings](GraphicsSettings.ja.md) にあります。

## ベイク、ビルド、ストリッピング

通常のオーサリングでは、**Bake Selected Scenes** でプロジェクトの保存済みシーンを処理・
保存します。**Bake Loaded Sources** はロード中のシーンをベイクしますが、保存するのは
アクティブシーンのみです。変更されたその他のシーンはご自身で保存してください。各コマンドの
対象、キャンセル、保存/Undo の挙動は
[オペレーションウィンドウのリファレンス](OperationsWindow.ja.md)で説明しています。

Play 時の自動ベイクとビルド時のベイクは、既定で有効です。Play 時のベイクはオーサリング
シーンを未保存状態 (dirty) にすることがあります。ビルド時のベイクはパイプラインが扱う
シーンのコピーに対して行われます。**BatchRendererGroup Variants = Keep All** を維持し、
出荷前にソースのストリッピングを確認してください。既定値、シェーダーの包含、任意の
Addressables 設定については [Graphics Settings](GraphicsSettings.ja.md) を参照してください。

## ランタイム API の要点

`StaticInstances` を使用します。メッシュレベルの範囲とソース階層は初期化前に設定します。
`OnEnable` でコンポーネントが初期化されます。これらの API は、ランタイムでのインスタンスの
追加・削除、メッシュの差し替え、Transform の編集を提供しません。

| API | 用途 |
| --- | --- |
| `StaticInstanceScatter.Visible`, `CastShadows`, `ShadowCastMaxLevel` | オーナーの可視性 (シャドウを含む) を制御する、またはシャドウのみを無効化/制限します。 |
| `MinDrawDistance`, `CullDistance` | オーサリングした近/遠の境界を更新します。値は非負で、制限なしには 0 を使い、Min/Max の整合を保ってください。 |
| `LodEnabled`, `LodMode`, `MinMeshLodLevel` | オーサリング済みのラダーを有効化/選択する、またはその最小レベルを変更します。`MaxMeshLodLevel` はオーサリング時の深さの設定です。 |
| `SetLodRatios(float[])`, `SetLodDistances(float[])` | 割合またはメートルで完全なラダーを渡します。オーサリング済みの遷移数と並び順に合わせてください。配列は渡されたとおりに受け付けます。アクセサーが返した配列を書き換えてもライブで更新されるとは想定しないでください。 |
| `FadeMode`, `CrossFadeWidth`, `LodFadeDuration` | フェードの挙動を更新します。duration が 0 の場合はプロジェクトの既定値に従います。 |
| `ForcedLodLevel`, `LodLevelOffset` | レベルを強制する (`-1` = Auto)、または自動選択をオフセットします。強制しても Draw Range のフェードは無効になりません。 |
| `IsInitialized`, `InstanceCount`, `DrawGroupCount`, `HasBakedData` | 初期化/ベイクの状態を確認します。シリアライズ済みのベイクがあっても、現在のソースに対して最新であることは保証されません。 |
| `StaticInstanceLodSystem.LodBias`, `LodDistanceScale`, `MinMeshLodLevel`, `CrossFadeEnabled` | ランタイムのグローバルオーバーライドです。プロジェクト設定は起動時に適用され、`StaticInstanceGlobalSettings.ApplyToSystem()` でオーサリングした値に戻せます。 |

`StaticInstanceLodSystem.RegisterDrivingCamera(camera)` でゲームプレイ用カメラを登録します。
戻り値の bool を確認してください。駆動カメラは最大 4 台までサポートします。明示的な登録が
ない場合は Camera.main がフォールバックになります。明示的に登録したカメラを外すときは
`UnregisterDrivingCamera` を、フォールバックに戻すときは `ClearDrivingCameras` を使います。
登録されていないビューは、他のカメラの LOD 判定を使って描画されることがあります。独立した
判定が必要なカメラはそれぞれ登録してください。

カメラカットの後は、メインスレッドで `StaticInstanceLodSystem.SnapFades(camera)` を
呼び出します。true はリクエストが受理されたことを意味します。次の対象となる評価で、
そのカメラの Time LOD と Draw Range のフェードが新しい位置に対して完了します。すでに
サブミット済みのビューは保持されます。描画を同期的に完了させるものではなく、他のカメラや
Distance/None のフェードには影響しません。

Editor の自動化には、公開されている `StaticInstanceBakeUtility` のベイク/検証/クリア操作を
使えます。内部のバッチクラスは公開の自動化用インターフェースではありません。

## 頂点アニメーション用のモーションベクトル

シェーダーが頂点をアニメーションさせ、TAA やモーションブラーのためにオブジェクトの
モーションが必要な場合は、初期化前に SIS Inspector の **Hysteresis** の下にある
**Motion Vector** を有効にします。このオプションは既定でオフです。変更には再初期化が
必要で、登録済みのバッチがライブで更新されることはありません。

マテリアルは、`MotionVectors` LightMode のパスを持つ BRG/DOTS インスタンシング互換の
URP シェーダーを使用する必要があります。その頂点アニメーション (VAT や手続き的な変形など) は、
現在と前フレームの両方の変形後位置を供給しなければなりません。SIS は固定のインスタンス単位の
Transform を提供しますが、VAT の前フレームの生成、任意のシェーダー変形のキャッシュ、
不足しているシェーダーパスの作成は行いません。変形しない標準のパスでは、独自に
アニメーションする頂点を正しく表現できません。SIS のカリングはベイク済みのバウンズを
使うため、ベイク前にアニメーションを包含するメッシュバウンズを設定してください。
インスタンスの Transform/メッシュ/マテリアルの参照は変更不可のままです。

このオプションを有効にすると、レンダラーがそのパスを要求したときにモーションベクトルの
描画処理が追加されます。このオプション自体が TAA やモーションブラーを有効にするわけでは
ありません。静的なオブジェクトでも、カメラが動けばカメラモーションは発生します。
オブジェクト単位の頂点モーションの出力はそれとは別の寄与です。風を取り除いた SpeedTree の
クローンを描画するグループは、追加のオブジェクト単位のパスを発行しません。これは他の
グループの独自頂点アニメーションのモーションベクトルには影響しません。

SIS はアニメーション再生システムや、任意のインスタンス単位のマテリアルプロパティの履歴を
提供しません。使用前に、マテリアル自身の前フレーム入力と、実際の Player での出力を、
ご自身のアニメーションで確認してください。

## SpeedTree コンテンツ

SIS は SpeedTree の風をシミュレーションせず、そのための設定もありません。マテリアルが
SpeedTree の風キーワード (`_WIND_SHARED`、`_WIND_BRANCH1`、`_WIND_BRANCH2`、
`_WIND_RIPPLE`、`_WIND_SHIMMER`) を有効にしているベイク済みのすべての描画グループに対し、
明示的なベイク操作 (**Bake Loaded Sources** と **Bake Selected Scenes**) は風を取り除いた
クローンマテリアルを `Assets/SIS_WindStatic/` 以下に生成し、SIS は代わりにそのクローンを
描画します。これにより SpeedTree の木は風の頂点処理コストなしで静止状態で描画されます。
ビルド時の自動ベイクは、既存の最新のクローンを再接続しますが、新しいクローンは作成できません。

クローンが生成されていないグループは元のマテリアルを使い続け、静止状態で描画されますが
(SIS がゼロ埋めした風のレコードを供給します)、風の頂点処理コストは引き続きかかります。
初期化時に、対処方法として再ベイクを案内する情報ログが出力されます。**Clear Baked Data** は
参照されなくなったクローンを削除します。

## 現在の制限事項と出荷前の確認

- 階層型 LOD (HLOD)、SIS の入れ子、スキン/半透明の描画、ライトマップ、LODGroup の
  直接描画、SIS が管理するインスタンス単位のマテリアルアニメーション、SIS と
  GPU Resident Drawer (GRD) による同時描画はサポートしていません。
- レンダラー単位の MaterialPropertyBlock による差分は保持されません。Validate はこれを
  報告します。共有マテリアルが、それらのオブジェクトごとの値を自動で再現することはありません。
- ワールド空間のシーンベイクは、自由に再配置できるプレハブデータではありません。
  ランタイムでのソース収集は開発用のフォールバックであり、ベイク済みシーンと同じ
  ロードコストにはなりません。
- 出荷前に、保存済みシーンのベイク、シェーダーの包含、ソースのストリッピング、カメラ、
  実際の Player を確認してください。動作検証済みの環境は、README 記載のバージョンでの
  Windows D3D12 です。ご自身のコンテンツでプロファイルしてください。GRD に対する一般的な
  性能上の優位性はうたっていません。
