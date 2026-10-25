# Models

Model files are not stored in git. Fetch them with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1
```

The script verifies each file's size and SHA-256, and Linkast checks them again before loading. Nothing is downloaded at runtime.

| File | Purpose | Source | License |
|---|---|---|---|
| `rvm_mobilenetv3_fp32.onnx` | Person segmentation for background blur, removal and replacement | [RobustVideoMatting v1.0.0](https://github.com/PeterL1n/RobustVideoMatting/releases/tag/v1.0.0) (Lin et al., 2021) | GPL-3.0 (`RVM-LICENSE.txt`) |
