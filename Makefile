# VocabSpire 构建与自动部署
# 需要 Git Bash / MSYS2 / WSL 环境下的 make
# 用法:
#   make build       - 构建 + 复制到游戏 mods 目录
#   make deploy      - 仅复制 DLL/PDB 到 mods 目录
#   make package     - 构建 + 打包 zip 到 publish/
#   make clean       - 清理构建产物

GAME_DIR  := D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2
MODS_DIR  := $(GAME_DIR)/mods/VocabSpire
PROJ_DIR  := VocabSpire
BUILD_DIR := $(PROJ_DIR)/.godot/mono/temp/bin/Release
PUBLISH_DIR := publish
PKG_DIR   := $(PUBLISH_DIR)/VocabSpire
PKG_ZIP   := $(PUBLISH_DIR)/VocabSpire.zip
WORD_BANKS := $(PROJ_DIR)/Resources/wordbanks

.PHONY: build deploy package clean

build:
	cd $(PROJ_DIR) && dotnet build -c Release
	cp '$(BUILD_DIR)/VocabSpire.dll'  '$(MODS_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.pdb'  '$(MODS_DIR)/'
	@echo "=== Build + Deploy OK ==="

deploy:
	cp '$(BUILD_DIR)/VocabSpire.dll'  '$(MODS_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.pdb'  '$(MODS_DIR)/'
	@echo "=== Deploy OK ==="

package:
	cd $(PROJ_DIR) && dotnet build -c Release
	rm -rf '$(PKG_DIR)'
	mkdir -p '$(PKG_DIR)/wordbanks'
	cp '$(BUILD_DIR)/VocabSpire.dll'           '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.pdb'           '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.json'          '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.deps.json'     '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/VocabSpire.runtimeconfig.json' '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/GodotSharp.dll'           '$(PKG_DIR)/'
	cp '$(BUILD_DIR)/ZstdSharp.dll'            '$(PKG_DIR)/'
	cp '$(PROJ_DIR)/vocab_icon.png'            '$(PKG_DIR)/' 2>/dev/null || true
	cp $(WORD_BANKS)/*.json '$(PKG_DIR)/wordbanks/'
	cp $(WORD_BANKS)/*.csv  '$(PKG_DIR)/wordbanks/' 2>/dev/null || true
	rm -f '$(PKG_ZIP)'
	cd '$(PUBLISH_DIR)' && zip -r VocabSpire.zip VocabSpire
	rm -rf '$(PKG_DIR)'
	@echo "=== Package OK: $(PKG_ZIP) ==="

clean:
	cd $(PROJ_DIR) && dotnet clean -c Release
	rm -rf '$(PUBLISH_DIR)'
	@echo "=== Clean OK ==="

