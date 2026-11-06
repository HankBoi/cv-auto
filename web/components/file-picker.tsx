"use client";

import { Button } from "@/components/ui/button";
import { useI18n } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { UploadCloud } from "lucide-react";
import { useId, useRef, useState } from "react";

/** Keyboard-reachable PDF picker with drag and drop. The server still validates every file. */
export function FilePicker({
  label,
  hint,
  multiple = false,
  disabled = false,
  buttonLabel,
  onFiles,
}: {
  label: string;
  hint?: string;
  multiple?: boolean;
  disabled?: boolean;
  buttonLabel?: string;
  onFiles: (files: File[]) => void;
}) {
  const { t } = useI18n();
  const inputId = useId();
  const hintId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);

  function take(list: FileList | null) {
    const files = Array.from(list ?? []);
    if (files.length > 0) onFiles(multiple ? files : files.slice(0, 1));
  }

  return (
    <div
      onDragOver={(event) => {
        if (disabled) return;
        event.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(event) => {
        event.preventDefault();
        setDragging(false);
        if (!disabled) take(event.dataTransfer.files);
      }}
      className={cn(
        "grid justify-items-center gap-2 rounded-2xl border border-dashed border-mint/35 bg-black/20 px-4 py-5 text-center transition-colors",
        dragging && "border-mint bg-blue-400/10",
        disabled && "opacity-60",
      )}
    >
      <UploadCloud aria-hidden="true" className="size-6 text-mint" />
      <label htmlFor={inputId} className="sr-only">
        {label}
      </label>
      <input
        ref={inputRef}
        id={inputId}
        type="file"
        accept="application/pdf,.pdf"
        multiple={multiple}
        disabled={disabled}
        aria-describedby={hint ? hintId : undefined}
        className="sr-only"
        onChange={(event) => {
          take(event.target.files);
          event.target.value = "";
        }}
      />
      <Button type="button" variant="outline" size="sm" disabled={disabled} onClick={() => inputRef.current?.click()}>
        {buttonLabel ?? label}
      </Button>
      <p className="text-xs text-muted">{t("picker.drop")}</p>
      {hint ? (
        <p id={hintId} className="text-xs text-muted">
          {hint}
        </p>
      ) : null}
    </div>
  );
}
