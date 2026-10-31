#!/usr/bin/env bash
# يستبدل كل ملفات المشروع بمحتوى الملف المضغوط الموجود في incoming/
# يحافظ على: .git  .github  incoming
set -euo pipefail
shopt -s nullglob dotglob

cd "$(git rev-parse --show-toplevel)"

zips=(incoming/*.zip)
if [ ${#zips[@]} -eq 0 ]; then echo "No zip in incoming/ - nothing to do."; exit 0; fi
if [ ${#zips[@]} -gt 1 ]; then echo "::error::Put exactly one zip in incoming/ (found ${#zips[@]})."; exit 1; fi
zipfile="${zips[0]}"
echo "Applying $zipfile"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unzip -q "$zipfile" -d "$work"

# تنظيف ملفات الأنظمة
rm -rf "$work/__MACOSX"
find "$work" -name '.DS_Store' -delete

# أمان: لا روابط رمزية ولا مسارات خارج المجلد
if find "$work" -type l | grep -q .; then echo "::error::Zip contains symlinks - rejected."; exit 1; fi

# إن كان للـ zip مجلد جذر واحد فقط نسطّحه
entries=("$work"/*)
if [ ${#entries[@]} -eq 1 ] && [ -d "${entries[0]}" ]; then src="${entries[0]}"; else src="$work"; fi

# حماية: يجب أن يشبه مشروعاً
if ! compgen -G "$src/*.sln" >/dev/null && [ ! -d "$src/src" ]; then
  echo "::error::Zip does not look like this project (no .sln and no src/). Rejected."; exit 1
fi

# لا يُسمح للـ zip بتغيير المجلدات المحمية
for p in .git .github incoming; do rm -rf "$src/$p"; done

# حذف الملفات الحالية (عدا المحمية)
for item in ./*; do
  name="$(basename "$item")"
  case "$name" in .git|.github|incoming) continue;; esac
  rm -rf "$item"
done

# نسخ الجديد
cp -a "$src"/. ./

# حذف الـ zip وتجهيز commit
rm -f "$zipfile"
git add -A

ver="unknown"; [ -f VERSION ] && ver="$(tr -d '[:space:]' < VERSION)"
echo "version=$ver" >> "${GITHUB_OUTPUT:-/dev/null}"
echo "Applied package, VERSION=$ver"
