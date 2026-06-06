# VocabSpire 构建与自动部署
# 需要 Git Bash / MSYS2 / WSL 环境下的 make
# 用法:
#   make build   - 构建 + 复制到游戏 mods 目录
#   make deploy  - 仅复制 DLL/PDB 到 mods 目录
#   make clean   - 清理构建产物

GAME_DIR  := D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2
MODS_DIR  := $(GAME_DIR)/mods/VocabSpire
PROJ_DIR  := VocabSpire
BUILD_DIR := $(PROJ_DIR)/.godot/mono/temp/bin/Release

.PHONY: build deploy clean

build:
	cd $(PROJ_DIR) && dotnet build -c Release
	cp '$(BUILD_DIR)/VocabSpire.dll'  '$(MODS_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.pdb'  '$(MODS_DIR)/'
	@echo "=== Build + Deploy OK ==="

deploy:
	cp '$(BUILD_DIR)/VocabSpire.dll'  '$(MODS_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.pdb'  '$(MODS_DIR)/'
	@echo "=== Deploy OK ==="

clean:
	cd $(PROJ_DIR) && dotnet clean -c Release
	@echo "=== Clean OK ==="

