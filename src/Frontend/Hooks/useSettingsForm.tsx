import { useCallback, useEffect, useRef, useState } from 'react';
import { useBlocker } from 'react-router-dom';
import { ApiError } from '../Services/api';
import { Modal } from '../Components/ui';

/**
 * Load, edit, validate, Save and Cancel for one settings section. `dirty` drives the unsaved-changes warning.
 * Validation runs here first and again on the server, which has the final say.
 */
export function useSettingsForm<T>(load: () => Promise<T>, save: (v: T) => Promise<T | void>, validate: (v: T) => Record<string, string>) {
  const [original, setOriginal] = useState<T>();
  const [value, setValue] = useState<T>();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [serverError, setServerError] = useState<ApiError | Error>();
  const [loadError, setLoadError] = useState<ApiError | Error>();
  const [saving, setSaving] = useState(false);
  const [savedAt, setSavedAt] = useState<number>();

  const reload = useCallback(() => {
    load().then((v) => { setOriginal(v); setValue(structuredClone(v)); setLoadError(undefined); }, (e) => setLoadError(e));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(reload, [reload]);

  const dirty = original !== undefined && value !== undefined && JSON.stringify(original) !== JSON.stringify(value);

  const onSave = async () => {
    if (value === undefined) return false;
    const problems = validate(value);
    setErrors(problems);
    setServerError(undefined);
    if (Object.keys(problems).length > 0) return false;
    setSaving(true);
    try {
      const saved = await save(value);
      const next = (saved ?? value) as T;
      setOriginal(next);
      setValue(structuredClone(next));
      setSavedAt(Date.now());
      return true;
    } catch (e) {
      setServerError(e as Error);
      return false;
    } finally { setSaving(false); }
  };

  const onCancel = () => { if (original !== undefined) setValue(structuredClone(original)); setErrors({}); setServerError(undefined); };

  return { value, setValue, dirty, errors, serverError, loadError, saving, savedAt, save: onSave, cancel: onCancel, reload };
}

/** Warns before leaving with unsaved changes: in-app navigation (a confirm dialog) and closing or reloading the tab. */
export function UnsavedChangesGuard({ dirty }: { dirty: boolean }) {
  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname);
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;

  useEffect(() => {
    const onBefore = (e: BeforeUnloadEvent) => { if (dirtyRef.current) { e.preventDefault(); e.returnValue = ''; } };
    window.addEventListener('beforeunload', onBefore);
    return () => window.removeEventListener('beforeunload', onBefore);
  }, []);

  if (blocker.state !== 'blocked') return null;
  return (
    <Modal title="Unsaved changes" onClose={() => blocker.reset()} footer={
      <>
        <button className="btn" onClick={() => blocker.reset()}>Stay on this page</button>
        <button className="btn btn-danger" onClick={() => blocker.proceed()}>Discard changes</button>
      </>}>
      <p>You have changes that have not been saved. If you leave now they will be lost.</p>
    </Modal>
  );
}
