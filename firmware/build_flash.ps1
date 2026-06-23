$env:IDF_PATH = "C:\Espressif\frameworks\esp-idf-v5.5.4"
$env:IDF_TOOLS_PATH = "C:\Espressif"
$pythonPath = "C:\Espressif\python_env\idf5.5_py3.11_env\Scripts"
$cmakePath = "C:\Espressif\tools\cmake\3.30.2\bin"
$ninjaPath = "C:\Espressif\tools\ninja\1.12.1"
$gitPath = "C:\Espressif\tools\idf-git\2.44.0\cmd"
$xtensaPath = "C:\Espressif\tools\xtensa-esp-elf\esp-14.2.0_20260121\xtensa-esp-elf\bin"
$riscvPath = "C:\Espressif\tools\riscv32-esp-elf\esp-14.2.0_20260121\riscv32-esp-elf\bin"

$env:PATH = "$pythonPath;$cmakePath;$ninjaPath;$gitPath;$xtensaPath;$riscvPath;" + $env:PATH
$env:PYTHONNOUSERSITE = "True"

# python $env:IDF_PATH\tools\idf.py fullclean
python $env:IDF_PATH\tools\idf.py build flash -p COM6 -b 460800
