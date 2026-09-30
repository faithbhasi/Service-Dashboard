import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { Drawer, Modal } from '../../src/Frontend/Components/ui';

function Layers({ onDrawerClose, onModalClose }: { onDrawerClose: () => void; onModalClose: () => void }) {
  const [modal, setModal] = useState(true);
  return (
    <Drawer open onClose={onDrawerClose} header={<h1>Pop-up</h1>}>
      <button onClick={() => setModal(true)}>Open dialog</button>
      {modal && <Modal title="Confirm" onClose={() => { onModalClose(); setModal(false); }}><p>Sure?</p></Modal>}
    </Drawer>
  );
}

describe('the Esc key closes one layer at a time', () => {
  it('closes only the dialog when one is open on top of a pop-up, then the pop-up on the next press', async () => {
    const u = userEvent.setup();
    const drawer = vi.fn(); const modal = vi.fn();
    render(<Layers onDrawerClose={drawer} onModalClose={modal} />);
    expect(screen.getByRole('dialog', { name: 'Confirm' })).toBeInTheDocument();
    await u.keyboard('{Escape}');
    expect(modal).toHaveBeenCalledTimes(1);
    expect(drawer).not.toHaveBeenCalled(); // the pop-up stays open behind the dialog
    await u.keyboard('{Escape}');
    expect(drawer).toHaveBeenCalledTimes(1);
  });
});
