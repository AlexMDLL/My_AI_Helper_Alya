# Cubism Native SDK (C++)

Сюда позже ляжет нативная DLL для рендера Live2D:

- `CubismSdkForNative` (официальный SDK, C++)
- обёртка `AlyaCubism.dll` с C-API:
  - `alya_cubism_init(modelPath)`
  - `alya_cubism_update(dt)`
  - `alya_cubism_draw(width, height)`
  - `alya_cubism_motion(group, index)`
  - `alya_cubism_parameter(id, value)`

C#-сторона подключает DLL через `DllImport` и отдаёт текстуру в Avalonia/OpenGL.

Лицензия Cubism SDK требует соблюдения условий Live2D — проверить перед публикацией.
