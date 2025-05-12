namespace Core.Model
{
    public interface IEntityObserver<TObservant> where TObservant : Entity
    {
        TObservant GetTarget();
        void ConnectToEntity(TObservant entity);
    }
}
