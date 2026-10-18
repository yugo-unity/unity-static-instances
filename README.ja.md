[English](README.md) | 日本語

# Static Instances

Unity URP 向けの静的シーンインスタンス描画パッケージです。インスタンス単位のメッシュ LOD、
可視性、シャドウ制御とオーサリングツールを備えます。本パッケージが提供する
**Static Instance Scatter (SIS)** コンポーネントは、静的な MeshRenderer のコンテンツを
BatchRendererGroup (BRG) API で描画します。

> [!NOTE]
> バージョン **0.1.0** は初期リリースです。API、シリアライズデータ、既定値は
> 1.0 までに変更される可能性があります。

## 動作要件

- Unity **6000.3** 以降と、Editor に対応する Universal Render Pipeline (URP) パッケージ
  (URP **17.3.0** 以降)。
- Burst、Collections、Mathematics は依存パッケージとして自動でインストールされます。
- Addressables は任意です。連携コードは互換性のある Addressables パッケージが
  インストールされている場合にのみコンパイルされます。

動作検証済みの環境は Windows + Direct3D 12 です。その他のプラットフォームや
グラフィックス API は現時点では検証対象外です。

## インストール

**Window > Package Manager** で **+ > Install package from git URL…** を選び、
次の URL を入力します。

```
https://github.com/yugo-unity/unity-static-instances.git
```

リリースを固定する場合は、末尾にタグを付けます (例:
`https://github.com/yugo-unity/unity-static-instances.git#v0.1.0`)。同じ URL を
プロジェクトの `Packages/manifest.json` の `dependencies` に追加することもできます。

```json
"jp.fuji0k0.static-instances": "https://github.com/yugo-unity/unity-static-instances.git#v0.1.0"
```

ローカルで開発する場合は、リポジトリをクローンし、**Install package from disk…** で
その `package.json` を指定します。

## プロジェクトの設定

1. **Project Settings > Graphics** と **Quality** に URP Asset を割り当てます。
2. **Project Settings > Graphics > Shader Stripping** で **BatchRendererGroup
   Variants** を **Keep All** に設定します。
3. SIS で描画するコンテンツには GPU Resident Drawer (GRD) を有効にしないでください。
   同じコンテンツを SIS と GRD で同時に描画する構成はサポートしていません。

## 最初のシーン

1. テスト用シーンを保存します。不透明で BRG/DOTS インスタンシング互換の MeshRenderer を
   親オブジェクトの下に置き、その親に **Rendering > Static Instance Scatter** を追加します。
   各ソースの **Batching Static** フラグはオフのままにし、SIS オーナーを入れ子にしないでください。
2. **Window > Rendering > Static Instances** を開いて **Validate** を実行し、
   未サポートのコンテンツを解消します。保存済みのシーンを選択し、**Bake Selected
   Scenes** でベイクと保存を行います。
3. Play に入って結果を確認します。シーンに必要なゲームプレイ用・物理用のオブジェクトは
   残してください。Player をビルドする前にソースのストリッピング設定を確認してください。

Transform、メッシュ、マテリアル、レンダリング属性は初期化後に変更できません。
オーサリング内容を変更した場合は、再ベイクと再初期化が必要です。LOD は既定で
無効です。メッシュ LOD レベルをオーサリング済みの場合は、まず **Screen Size** による選択と
**Time** フェードから始めてください。Draw Range はメッシュ LOD とは独立して機能します。

**Motion Vector** は VAT などのシェーダー頂点アニメーション向けの、既定でオフの
初期化時オプションです。シェーダーは `LightMode=MotionVectors` タグ付きのパスを持ち、
現在と前フレームの両方の変形後位置を計算する必要があります。このオプションを
有効にしても、マテリアルやインスタンスの Transform がアニメーションするわけではありません。

SpeedTree 9 のコンテンツは静止状態で描画されます。SIS は風のシミュレーションを行わず、
明示的なベイク操作によって風を取り除いたマテリアルが生成されます。

## ドキュメント

- [ユーザーガイド](Docs/UserGuide.ja.md): シーンへの導入、ランタイム API、ワークフロー。
- [SIS Inspector](Docs/StaticInstanceScatterInspector.ja.md): コンポーネントごとの設定。
- [Graphics Settings](Docs/GraphicsSettings.ja.md): プロジェクト全体の品質、ベイク、
  ストリッピングのポリシー。
- [オペレーションウィンドウ](Docs/OperationsWindow.ja.md): 検証、ベイク、変換、
  シーンの一括処理。

## 制限事項

- 非サポート: 階層型 LOD (HLOD)、SIS オーナーの入れ子、スキン/半透明の描画、
  ライトマップ、LODGroup の直接描画 (先に変換が必要)、レンダラー単位の
  MaterialPropertyBlock による差分、同一コンテンツの SIS/GRD 同時描画。
- ワールド空間のベイクは、再配置可能なプレハブデータではありません。
- GRD に対する一般的な性能上の優位性や、アロケーションゼロは保証していません。
  ご自身のコンテンツでプロファイルしてください。

出荷前に、保存済みのベイク、シェーダーの包含、ストリッピング、カメラ、実際の Player を
ご自身のコンテンツで確認してください。

## テスト

Editor テストが含まれています。Test Runner に表示するには、プロジェクトの
`Packages/manifest.json` のルートにある `testables` にパッケージを追加します。

```json
"testables": ["jp.fuji0k0.static-instances"]
```

BRG のライフサイクルテストを実行する際は、URP Asset を割り当てた状態で実行してください。

## ライセンス

[MIT](LICENSE)
